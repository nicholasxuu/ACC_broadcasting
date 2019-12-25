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

        public bool isAutoSwitchFocusCarMode = false;
        private int _autoSwitchMinimumInterval = 1; // seconds 
        private long _autoSwitchLastSwitchTime = 0;
        public UInt16 currentFocusedCarIndex = 0;

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
                    this.currentFocusedCarIndex = newFocusedCarIndex;
                }
            }
        }

        private void RequestToggleAutoFocusCar(object obj)
        {
            this.isAutoSwitchFocusCarMode = !this.isAutoSwitchFocusCarMode;
        }

        private void RequestEnableSwitchAutoFocusCar(object obj)
        {
            this.isAutoSwitchFocusCarMode = true;
        }

        private void RequestDisableSwitchAutoFocusCar(object obj)
        {
            this.isAutoSwitchFocusCarMode = false;
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

            if (this.isAutoSwitchFocusCarMode)
            {
                try
                {
                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (currentTime - this._autoSwitchLastSwitchTime > this._autoSwitchMinimumInterval)
                    {
            

                        var sortedCarsByGapMeters = Cars.Where(x => x.GapFrontMeters > 0 && x.CarLocation != CarLocationEnum.Pitlane).OrderBy(x => x.GapFrontMeters).ToArray();
                        var carWithSmallestGapFront = sortedCarsByGapMeters[0];

                        var newFocusedCarIndex = Convert.ToUInt16(carWithSmallestGapFront.CarIndex);
                        foreach(var client in _clients)
                        {
                            client.SetFocus(newFocusedCarIndex);
                        }
            
                        this.currentFocusedCarIndex = newFocusedCarIndex;
                        this._autoSwitchLastSwitchTime = currentTime;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
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
