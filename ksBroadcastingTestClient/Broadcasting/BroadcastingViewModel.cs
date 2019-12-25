using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ksBroadcastingNetwork;
using ksBroadcastingNetwork.Structs;

namespace ksBroadcastingTestClient.Broadcasting
{
    public class BroadcastingViewModel : KSObservableObject
    {
        public ObservableCollection<CarViewModel> Cars { get; } = new ObservableCollection<CarViewModel>();
        public TrackViewModel TrackVM { get => Get<TrackViewModel>(); private set => Set(value); }
        public KSRelayCommand RequestFocusedCarCommand { get; }

        public KSRelayCommand RequestToggleAutoFocusCarCommand { get; }

        public KSRelayCommand RequestEnableSwitchAutoFocusCarCommand { get; }

        public KSRelayCommand RequestDisableSwitchAutoFocusCarCommand { get; }

        private List<BroadcastingNetworkProtocol> _clients = new List<BroadcastingNetworkProtocol>();

        public bool IsAutoSwitchFocusCarMode = false;
        private int _autoSwitchMinimumInterval = 8; // seconds 
        private long _autoSwitchLastSwitchTime = 0;
        public UInt16 CurrentFocusedCarIndex = 0;

        public BroadcastingViewModel()
        {
            RequestFocusedCarCommand = new KSRelayCommand(RequestFocusedCar);
            RequestToggleAutoFocusCarCommand = new KSRelayCommand(RequestToggleAutoFocusCar);
            RequestEnableSwitchAutoFocusCarCommand = new KSRelayCommand(RequestEnableSwitchAutoFocusCar);
            RequestDisableSwitchAutoFocusCarCommand = new KSRelayCommand(RequestDisableSwitchAutoFocusCar);
        }

        private void RequestFocusedCar(object obj)
        {
            var car = obj as CarViewModel;
            if (car != null)
            {
                foreach (var client in _clients)
                {
                    // mssing readonly check, will skip this as the ACC client has to handle this as well
                    var newFocusedCarIndex = Convert.ToUInt16(car.CarIndex);
                    client.SetFocus(newFocusedCarIndex);
                    this.CurrentFocusedCarIndex = newFocusedCarIndex;
                }
            }
        }

        private void RequestToggleAutoFocusCar(object obj)
        {
            this.IsAutoSwitchFocusCarMode = !this.IsAutoSwitchFocusCarMode;
        }

        private void RequestEnableSwitchAutoFocusCar(object obj)
        {
            this.IsAutoSwitchFocusCarMode = true;
        }

        private void RequestDisableSwitchAutoFocusCar(object obj)
        {
            this.IsAutoSwitchFocusCarMode = false;
        }

        private void RequestHudPageChange(string requestedHudPage)
        {
            foreach (var client in _clients)
            {
                // mssing readonly check, will skip this as the ACC client has to handle this as well
                client.RequestHUDPage(requestedHudPage);
            }
        }

        private void RequestCameraChange(string camSet, string camera)
        {
            foreach (var client in _clients)
            {
                // mssing readonly check, will skip this as the ACC client has to handle this as well
                client.SetCamera(camSet, camera);
            }
        }

        internal void RegisterNewClient(ACCUdpRemoteClient newClient)
        {
            if (newClient.MsRealtimeUpdateInterval > 0)
            {
                // This client will send realtime updates, we should listen
                newClient.MessageHandler.OnTrackDataUpdate += MessageHandler_OnTrackDataUpdate;
                newClient.MessageHandler.OnEntrylistUpdate += MessageHandler_OnEntrylistUpdate;
                newClient.MessageHandler.OnRealtimeUpdate += MessageHandler_OnRealtimeUpdate;
                newClient.MessageHandler.OnRealtimeCarUpdate += MessageHandler_OnRealtimeCarUpdate;
            }

            _clients.Add(newClient.MessageHandler);
        }

        private void MessageHandler_OnTrackDataUpdate(string sender, TrackData trackUpdate)
        {
            if (TrackVM?.TrackId != trackUpdate.TrackId)
            {
                if (TrackVM != null)
                {
                    TrackVM.OnRequestCameraChange -= RequestCameraChange;
                    TrackVM.OnRequestHudPageChange -= RequestHudPageChange;
                }


                TrackVM = new TrackViewModel(trackUpdate.TrackId, trackUpdate.TrackName, trackUpdate.TrackMeters);
                TrackVM.OnRequestCameraChange += RequestCameraChange;
                TrackVM.OnRequestHudPageChange += RequestHudPageChange;
            }

            // The track cams may update in between
            TrackVM.Update(trackUpdate);
        }

        private void MessageHandler_OnEntrylistUpdate(string sender, CarInfo carUpdate)
        {
            CarViewModel vm = Cars.SingleOrDefault(x => x.CarIndex == carUpdate.CarIndex);
            if (vm == null)
            {
                vm = new CarViewModel(carUpdate.CarIndex);
                Cars.Add(vm);
            }

            vm.Update(carUpdate);
        }

        private void MessageHandler_OnRealtimeUpdate(string sender, RealtimeUpdate update)
        {
            if (TrackVM != null)
                TrackVM.Update(update);

            foreach (var carVM in Cars)
            {
                carVM.SetFocused(update.FocusedCarIndex);
            }

            try
            {
                if (TrackVM?.TrackMeters > 0)
                {
                    var sortedCars = Cars.OrderBy(x => x.SplinePosition).ToArray();
                    for (int i = 1; i < sortedCars.Length; i++)
                    {
                        var carAhead = sortedCars[i - 1];
                        var carBehind = sortedCars[i];
                        var splineDistance = Math.Abs(carAhead.SplinePosition - carBehind.SplinePosition);
                        while (splineDistance > 1f)
                            splineDistance -= 1f;

                        carBehind.GapFrontMeters = splineDistance * TrackVM.TrackMeters;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            if (this.IsAutoSwitchFocusCarMode)
            {
                this.handleAutoSwitchCar();
            }

        }

        private int calculateBroadcastWeight(CarViewModel currCar, CarViewModel carFront)
        {
            // broadcaseWeight (higher better) = car_hot_level - car_cooldown_level
            // car_hot_level = 1 / GapFrontMeters (smaller better) + 1 / position (smaller better) + pit_exit_bonus + front_car_pit_exit_bonus(++bonus)
            // GapFrontMeters: 1 must watch - 10000, 10 hot battle - 100, 50 close - 70, 100 okay - 40, 200 hardly visible - 0
            // position: 1-10 has point bonus of: 25	18	15	12	10	8	6	4	2	1
            // car_cooldown_level = recent_focused_time (higher better)

            var currWeight = 0;
            if (currCar.GapFrontMeters <= 1)
            {
                currWeight += 10000;
            }
            else if (currCar.GapFrontMeters <= 5)
            {
                currWeight += 1000;
            }
            else if (currCar.GapFrontMeters <= 10)
            {
                currWeight += 100;
            }
            else if (currCar.GapFrontMeters <= 30)
            {
                currWeight += 90;
            }
            else if (currCar.GapFrontMeters <= 50)
            {
                currWeight += 70;
            }
            else if (currCar.GapFrontMeters <= 100)
            {
                currWeight += 40 + (int)((currCar.GapFrontMeters - 50) / (100 - 50) * (70 - 40));
            }
            else if (currCar.GapFrontMeters <= 200)
            {
                currWeight += 0 + (int)((currCar.GapFrontMeters - 100) / (200 - 100) * (40 - 0));
            }

            var positionBonuses = new int[10] { 25, 18, 15, 12, 10, 8, 6, 4, 2, 1 };
            if (carFront.Position <= 10)
            {
                currWeight += positionBonuses[carFront.Position - 1];
            }

            if (currCar.CarLocation == CarLocationEnum.PitEntry)
            {
                currWeight += 5;
            }

            if (currCar.CarLocation == CarLocationEnum.PitExit)
            {
                currWeight += 10;
            }

            if (carFront.CarLocation == CarLocationEnum.PitExit && currCar.GapFrontMeters < 500)
            {
                currWeight += 20;

                if (currCar.GapFrontMeters < 200)
                {
                    currWeight += 30;
                }
            }

            return currWeight - currCar.BroadcastTimeWeightDeduction;
        }

        private void handleAutoSwitchCar()
        {
            try
            {
                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (currentTime - this._autoSwitchLastSwitchTime < this._autoSwitchMinimumInterval)
                {
                    return;
                }



                var sortedCars = Cars.OrderBy(x => x.SplinePosition).ToArray();
                for (int i = 1; i < sortedCars.Length; i++)
                {
                    var carAhead = sortedCars[i - 1];
                    var carBehind = sortedCars[i];
                    var splineDistance = Math.Abs(carAhead.SplinePosition - carBehind.SplinePosition);
                    while (splineDistance > 1f)
                        splineDistance -= 1f;

                    carBehind.GapFrontMeters = splineDistance * TrackVM.TrackMeters;

                    var carBehindCarIndex = Convert.ToUInt16(carBehind.CarIndex);
                    if (CurrentFocusedCarIndex != carBehindCarIndex)
                    {
                        carBehind.BroadcastTimeWeightDeduction = carBehind.BroadcastTimeWeightDeduction / 2;
                    }
                    else
                    {
                        carBehind.BroadcastTimeWeightDeduction = (int) (currentTime - this._autoSwitchLastSwitchTime);
                    }


                    carBehind.BroadcastWeight = this.calculateBroadcastWeight(carBehind, carAhead);
                }

                var sortedCarsByGapMeters = Cars
                    .Where(x => x.GapFrontMeters > 0 && x.CarLocation != CarLocationEnum.Pitlane && x.Kmh > 0) // quick filter out disabled cars.
                    .OrderBy(a => Guid.NewGuid()) // random shuffle so equal weight cars can get randomized
                    .OrderByDescending(x => Math.Max(x.BroadcastWeight - x.BroadcastTimeWeightDeduction, 0)) // sort to find max weight
                    .ToArray();

                var newFocusedCar = sortedCarsByGapMeters.FirstOrDefault();

                var newFocusedCarIndex = Convert.ToUInt16(newFocusedCar.CarIndex);


                if (this.CurrentFocusedCarIndex == newFocusedCarIndex)
                {
                    foreach (var client in _clients)
                    {
                        client.SetFocus(newFocusedCarIndex);
                    }

                    this.CurrentFocusedCarIndex = newFocusedCarIndex;
                    this._autoSwitchLastSwitchTime = currentTime;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private void MessageHandler_OnRealtimeCarUpdate(string sender, RealtimeCarUpdate carUpdate)
        {
            var vm = Cars.FirstOrDefault(x => x.CarIndex == carUpdate.CarIndex);
            if (vm == null)
            {
                // Oh, we don't have this car yet. In this implementation, the Network protocol will take care of this
                // so hopefully we will display this car in the next cycles
                return;
            }

            vm.Update(carUpdate);
        }
    }
}
