// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Concurrency
{
    // Settings specific to a simulation and the hub SKU in use
    public interface IRateLimitingConfig
    {
        int RegistryOperationsPerMinute { get; }
        int TwinReadsPerSecond { get; }
        int TwinWritesPerSecond { get; }
        int ConnectionsPerSecond { get; }
        int DeviceMessagesPerSecond { get; }
        long DeviceMessagesPerDay { get; }
    }

    public class RateLimitingConfig : IRateLimitingConfig
    {
        public int RegistryOperationsPerMinute { get; set; }
        public int TwinReadsPerSecond { get; set; }
        public int TwinWritesPerSecond { get; set; }
        public int ConnectionsPerSecond { get; set; }
        public int DeviceMessagesPerSecond { get; set; }
        public long DeviceMessagesPerDay { get; set; }
    }
}
