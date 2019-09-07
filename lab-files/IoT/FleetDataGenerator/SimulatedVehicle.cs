﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CosmosDbIoTScenario.Common;
using CosmosDbIoTScenario.Common.Models;
using Microsoft.Azure.EventHubs;
using Newtonsoft.Json;

namespace FleetDataGenerator
{
    public class SimulatedVehicle
    {
        // The amount of time to delay between sending telemetry.
        private readonly TimeSpan CycleTime = TimeSpan.FromMilliseconds(100);
        private int _messagesSent = 0;
        private int _vehicleNumber = 0;
        private bool _causeRefrigerationUnitFailure = false;
        private bool _immediateRefrigerationUnitFailure = false;
        private double _distanceRemaining = 0;
        private double _distanceTraveled = 0;
        private readonly Trip _trip;
        private readonly string _tripId;
        private readonly EventHubClient _eventHubClient;
        private CancellationTokenSource _localCancellationSource = new CancellationTokenSource();

        public string TripId => _tripId;

        /// <summary>
        /// The total number of messages sent by this device to Event Hubs.
        /// </summary>
        public int MessagesSent => _messagesSent;

        public SimulatedVehicle(Trip trip, bool causeRefrigerationUnitFailure,
            bool immediateRefrigerationUnitFailure, string eventHubsConnectionString,
            int vehicleNumber)
        {
            _vehicleNumber = vehicleNumber;
            _trip = trip;
            _tripId = trip.id;
            _distanceRemaining = trip.plannedTripDistance + 2; // Pad a little bit extra distance to ensure all events captured.
            _causeRefrigerationUnitFailure = causeRefrigerationUnitFailure;
            _immediateRefrigerationUnitFailure = immediateRefrigerationUnitFailure;
            _eventHubClient = EventHubClient.CreateFromConnectionString(eventHubsConnectionString);
        }

        /// <summary>
        /// Creates an asynchronous task for sending all data for the vehicle.
        /// </summary>
        /// <returns>Task for asynchronous device operation</returns>
        public async Task RunVehicleSimulationAsync()
        {
            await SendDataToHub(_localCancellationSource.Token).ConfigureAwait(false);
        }

        public void CancelCurrentRun()
        {
            _localCancellationSource.Cancel();
        }

        /// <summary>
        /// Takes a set of RefrigerationUnitTelemetryItem data for a device in a dataset and sends the
        /// data to the message with a configurable delay between each message.
        /// </summary>
        /// <param name="RefrigerationUnitTelemetry">The set of data to send as messages to the IoT Central.</param>
        /// <returns></returns>
        private async Task SendDataToHub(CancellationToken cancellationToken)
        {
            // Generate simulated refrigeration unit data.
            const int sampleSize = 10000;
            const int failOverXIterations = 625;
            var vehicleTelemetryGenerator = new VehicleTelemetryGenerator(_trip.vin);
            var telemetryTimer = new Stopwatch();
            var refrigerationTelemetry = DataGenerator.GenerateRefrigerationUnitTelemetry(_trip.temperatureSetting, sampleSize,
                _causeRefrigerationUnitFailure, _immediateRefrigerationUnitFailure ? 0 : failOverXIterations).ToArray();

            var refrigerationTelemetryCount = refrigerationTelemetry.Length;
            var idx = 0;
            var outsideTemperature = vehicleTelemetryGenerator.GetOutsideTemp(_trip.location);

            telemetryTimer.Start();
            while (!_localCancellationSource.IsCancellationRequested && _distanceRemaining >= 0)
            {
                // Reset the refrigeration unit telemetry if we've run out of items. This will also reset failed events, if applicable.
                if (idx >= refrigerationTelemetryCount) idx = 0;
                var vehicleTelemetry = vehicleTelemetryGenerator.GenerateMessage(_trip.location, outsideTemperature);
                vehicleTelemetry.refrigerationUnitKw = refrigerationTelemetry[idx].RefrigerationUnitKw;
                vehicleTelemetry.refrigerationUnitTemp = refrigerationTelemetry[idx].RefrigerationUnitTemp;
                var distanceTraveled = Helpers.DistanceTraveled(vehicleTelemetry.speed, telemetryTimer.ElapsedMilliseconds);
                telemetryTimer.Restart();
                
                _distanceTraveled += distanceTraveled;
                _distanceRemaining -= distanceTraveled;
                vehicleTelemetry.odometer = Math.Round(_trip.odometerBegin + _distanceTraveled, 2);

                // Serialize data and send to Event Hubs:
                await SendEvent(JsonConvert.SerializeObject(vehicleTelemetry), cancellationToken).ConfigureAwait(false);

                idx++;

                await Task.Delay(CycleTime, cancellationToken).ConfigureAwait(false);
            }

            if (_distanceRemaining < 0)
            {
                Program.WriteLineInColor($"Vehicle {_vehicleNumber} has completed its trip.", ConsoleColor.Yellow);
            }

            telemetryTimer.Stop();
        }

        /// <summary>
        /// Uses the EventHubClient to send a message to Event Hubs.
        /// </summary>
        /// <param name="message">JSON string representing serialized telemetry data.</param>
        /// <returns>Task for async execution.</returns>
        private async Task SendEvent(string message, CancellationToken cancellationToken)
        {
            using (var eventData = new EventData(Encoding.ASCII.GetBytes(message)))
            {
                // Send telemetry to Event Hubs, using the vehicle's VIN as the partition key to guarantee message ordering.
                await _eventHubClient.SendAsync(eventData: eventData,
                    partitionKey: _trip.vin).ConfigureAwait(false);

                // Keep track of messages sent and update progress periodically.
                var currCount = Interlocked.Increment(ref _messagesSent);
                if (currCount % 50 == 0)
                {
                    Console.WriteLine($"Vehicle {_vehicleNumber}: {_trip.vin} Message count: {currCount} -- {Math.Round(_distanceRemaining, 2)} miles remaining");
                }
            }
        }
    }
}