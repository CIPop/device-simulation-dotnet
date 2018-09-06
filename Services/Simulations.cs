// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Diagnostics;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Exceptions;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.IotHub;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Models;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Runtime;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Storage;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.StorageAdapter;
using Newtonsoft.Json;

namespace Microsoft.Azure.IoTSolutions.DeviceSimulation.Services
{
    public interface ISimulations
    {
        /// <summary>
        /// Get list of simulations.
        /// </summary>
        Task<IList<Models.Simulation>> GetListAsync();

        /// <summary>
        /// Get a simulation.
        /// </summary>
        Task<Models.Simulation> GetAsync(string id);

        /// <summary>
        /// Create a simulation.
        /// </summary>
        Task<Models.Simulation> InsertAsync(Models.Simulation simulation, string template = "");

        /// <summary>
        /// Create or Replace a simulation.
        /// </summary>
        Task<Models.Simulation> UpsertAsync(Models.Simulation simulation);

        /// <summary>
        /// Modify a simulation.
        /// </summary>
        Task<Models.Simulation> MergeAsync(SimulationPatch patch);

        /// <summary>
        /// Add a device to simulation
        /// </summary>
        Task AddDeviceAsync(string id);

        /// <summary>
        /// Delete a simulation and its devices.
        /// </summary>
        Task DeleteAsync(string id);

        /// <summary>
        /// Get the ID of the devices in a simulation.
        /// </summary>
        IEnumerable<string> GetDeviceIds(Models.Simulation simulation);
    }

    public class Simulations : ISimulations
    {
        private const string DEFAULT_SIMULATION_ID = "1";
        private const string DEFAULT_TEMPLATE_NAME = "default";
        private const string DEVICES_COLLECTION = "SimulatedDevices";
        private const int DEVICES_PER_MODEL_IN_DEFAULT_TEMPLATE = 1;

        private readonly IDeviceModels deviceModels;
        private readonly IStorageAdapterClient storageAdapterClient;
        private readonly IStorageRecords simulationsStorage;
        private readonly IIotHubConnectionStringManager connectionStringManager;
        private readonly IDevices devices;
        private readonly ILogger log;

        public Simulations(
            IServicesConfig config,
            IDeviceModels deviceModels,
            IFactory factory,
            IStorageAdapterClient storageAdapterClient,
            IIotHubConnectionStringManager connectionStringManager,
            IDevices devices,
            ILogger logger)
        {
            this.deviceModels = deviceModels;
            this.storageAdapterClient = storageAdapterClient;
            this.simulationsStorage = factory.Resolve<IStorageRecords>().Init(config.SimulationsStorage);
            this.connectionStringManager = connectionStringManager;
            this.devices = devices;
            this.log = logger;
        }

        /// <summary>
        /// Get list of simulations.
        /// </summary>
        public async Task<IList<Models.Simulation>> GetListAsync()
        {
            var items = await this.simulationsStorage.GetAllAsync();
            var result = new List<Models.Simulation>();
            foreach (var item in items)
            {
                var simulation = JsonConvert.DeserializeObject<Models.Simulation>(item.Data);
                simulation.ETag = item.ETag;
                simulation.Id = item.Id;
                result.Add(simulation);
            }

            // TODO: This will need changes to support pagination. Also order should be by simulation Id.
            return result.OrderByDescending(s => s.Created).ToList();
        }

        /// <summary>
        /// Get a simulation.
        /// </summary>
        public async Task<Models.Simulation> GetAsync(string id)
        {
            var item = await this.simulationsStorage.GetAsync(id);
            if (item == null) return null;

            var simulation = JsonConvert.DeserializeObject<Models.Simulation>(item.Data);
            simulation.ETag = item.ETag;
            simulation.Id = item.Id;
            return simulation;
        }

        /// <summary>
        /// Create a simulation.
        /// </summary>
        public async Task<Models.Simulation> InsertAsync(Models.Simulation simulation, string template = "")
        {
            var usingDefaultTemplate = !string.IsNullOrEmpty(template) && template.ToLowerInvariant() == DEFAULT_TEMPLATE_NAME;

            if (!string.IsNullOrEmpty(template) && template.ToLowerInvariant() != DEFAULT_TEMPLATE_NAME)
            {
                this.log.Warn("Unknown template name", () => new { template });
                throw new InvalidInputException("Unknown template name. Try 'default'.");
            }

            if (!usingDefaultTemplate && string.IsNullOrEmpty(simulation.Name))
            {
                this.log.Warn("Missing simulation name", () => new { });
                throw new InvalidInputException("Simulation name is required.");
            }

            // Note: forcing the ID because only one simulation can be created
            simulation.Id = usingDefaultTemplate ? DEFAULT_SIMULATION_ID : Guid.NewGuid().ToString();
            simulation.Created = DateTimeOffset.UtcNow;
            simulation.Modified = simulation.Created;

            // Create default simulation
            if (usingDefaultTemplate)
            {
                simulation.Name = "Default Simulation";
                var types = await this.deviceModels.GetListAsync();
                simulation.DeviceModels = new List<Models.Simulation.DeviceModelRef>();
                foreach (var type in types)
                {
                    simulation.DeviceModels.Add(new Models.Simulation.DeviceModelRef
                    {
                        Id = type.Id,
                        Count = DEVICES_PER_MODEL_IN_DEFAULT_TEMPLATE
                    });
                }
            }

            var iotHubConnectionStrings = new List<string>(simulation.IotHubConnectionStrings);
            simulation.IotHubConnectionStrings.Clear();
            foreach (var iotHubConnectionString in iotHubConnectionStrings)
            {
                var connString = await this.connectionStringManager.RedactAndStoreAsync(iotHubConnectionString);
                simulation.IotHubConnectionStrings.Add(connString);
            }

            // This value cannot be set by the user, so we set it here
            // TODO: Can this be removed as this value is set in the Simulation constructor?
            simulation.PartitioningComplete = false;

            var result = await this.simulationsStorage.CreateAsync(
                new StorageRecord
                {
                    Id = simulation.Id,
                    Data = JsonConvert.SerializeObject(simulation)
                }
            );

            simulation.ETag = result.ETag;
            simulation.Id = result.Id;

            return simulation;
        }

        /// <summary>
        /// Create or Replace a simulation.
        /// The logic works under the assumption that there is only one simulation with id "1".
        /// </summary>
        public async Task<Models.Simulation> UpsertAsync(Models.Simulation simulation)
        {
            if (string.IsNullOrEmpty(simulation.Id))
            {
                throw new InvalidInputException("Simulation ID is not specified.");
            }

            var existingSimulation = await this.GetAsync(simulation.Id);
            if (existingSimulation != null)
            {
                this.log.Info("Modifying simulation");

                if (simulation.ETag == "*")
                {
                    simulation.ETag = existingSimulation.ETag;
                    this.log.Warn("The client used ETag='*' choosing to overwrite the current simulation");
                }

                if (simulation.ETag != existingSimulation.ETag)
                {
                    this.log.Error("Invalid ETag. Running simulation ETag is:'", () => new { simulation });
                    throw new ResourceOutOfDateException("Invalid ETag. Running simulation ETag is:'" + simulation.ETag + "'.");
                }

                simulation.Created = existingSimulation.Created;
                simulation.Modified = DateTimeOffset.UtcNow;
            }
            else
            {
                this.log.Info("Creating new simulation");
                // new simulation
                simulation.Created = DateTimeOffset.UtcNow;
                simulation.Modified = simulation.Created;
            }

            for (var index = 0; index < simulation.IotHubConnectionStrings.Count; index++)
            {
                var connString = await this.connectionStringManager.RedactAndStoreAsync(simulation.IotHubConnectionStrings[index]);
                simulation.IotHubConnectionStrings[index] = connString;
            }

            var result = await this.simulationsStorage.UpsertAsync(
                new StorageRecord
                {
                    Id = simulation.Id,
                    Data = JsonConvert.SerializeObject(simulation)
                }
            );

            // Return the new ETag provided from storage
            simulation.ETag = result.ETag;
            simulation.Id = result.Id;

            return simulation;
        }

        /// <summary>
        /// Modify a simulation.
        /// </summary>
        public async Task<Models.Simulation> MergeAsync(SimulationPatch patch)
        {
            if (string.IsNullOrEmpty(patch.Id))
            {
                this.log.Warn("Invalid simulation ID.", () => new { patch.Id });
                throw new InvalidInputException("Invalid simulation ID.");
            }

            var item = await this.simulationsStorage.GetAsync(patch.Id);
            var simulation = JsonConvert.DeserializeObject<Models.Simulation>(item.Data);
            simulation.ETag = item.ETag;
            simulation.Id = item.Id;

            // Even when there's nothing to do, verify the ETag mismatch
            if (patch.ETag != simulation.ETag)
            {
                this.log.Warn("ETag mismatch",
                    () => new { Current = simulation.ETag, Provided = patch.ETag });
                throw new ConflictingResourceException(
                    $"The ETag provided doesn't match the current resource ETag ({simulation.ETag}).");
            }

            if (!patch.Enabled.HasValue || patch.Enabled.Value == simulation.Enabled)
            {
                // Nothing to do
                return simulation;
            }

            simulation.Enabled = patch.Enabled.Value;
            simulation.Modified = DateTimeOffset.UtcNow;

            if (patch.Enabled == false)
            {
                simulation.StoppedTime = simulation.Modified;
                simulation.Statistics = new Models.Simulation.StatisticsRef
                {
                    AverageMessagesPerSecond = patch.Statistics.AverageMessagesPerSecond,
                    TotalMessagesSent = patch.Statistics.TotalMessagesSent
                };
            }

            item = await this.simulationsStorage.UpsertAsync(
                new StorageRecord
                {
                    Id = simulation.Id,
                    Data = JsonConvert.SerializeObject(simulation)
                },
                patch.ETag
            );

            simulation.ETag = item.ETag;

            return simulation;
        }

        /// <summary>
        /// Delete a simulation and its devices.
        /// </summary>
        public async Task DeleteAsync(string id)
        {
            // Delete devices first
            var deviceIds = this.GetDeviceIds(await this.GetAsync(id));
            await this.devices.DeleteListAsync(deviceIds);

            // Then delete the simulation from storage
            await this.simulationsStorage.DeleteAsync(id);
        }

        public async Task AddDeviceAsync(string id)
        {
            await this.storageAdapterClient.CreateAsync(DEVICES_COLLECTION, id, id);
        }

        /// <summary>
        /// Get the ID of the devices in a simulation.
        /// </summary>
        public IEnumerable<string> GetDeviceIds(Models.Simulation simulation)
        {
            var deviceIds = new List<string>();

            // Calculate the device IDs used in the simulation
            var models = (from model in simulation.DeviceModels where model.Count > 0 select model).ToList();
            foreach (var model in models)
            {
                for (var i = 0; i < model.Count; i++)
                {
                    deviceIds.Add(this.devices.GenerateId(model.Id, i));
                }
            }

            return deviceIds;
        }
    }
}
