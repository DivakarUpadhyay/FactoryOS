using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Connectors.Simulation;
using Divakar.FactoryOS.Domain.Plant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Divakar.FactoryOS.UnitTests;

public sealed class SimulatorAndEngineTests
{
    private static TagBinding Tag(string code, string sensor, string profile, double? min = null, double? max = null, double deadband = 0) =>
        new(Guid.NewGuid(), code, sensor, profile, null, 1, 0, deadband, min, max);

    private static AssetBinding Asset(string type, decimal? kw, params TagBinding[] tags) =>
        new(Guid.NewGuid(), Guid.NewGuid(), $"{type}-1", type, "simulator", null, null, 1000, kw, tags);

    // 02:00 UTC = 10:00 in Taiwan, a busy day shift
    private static readonly DateTime Day = new(2026, 10, 16, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Counters_keep_rising_when_the_simulator_restarts_later()
    {
        var energy = Tag("energy", "energy_kwh", "counter_energy");
        var parts = Tag("parts", "part_count", "counter_parts");
        var asset = Asset("cnc", 22, energy, parts);
        var before = new FactorySimulator(asset, Day, seed: 3).Step(Day.AddSeconds(60)).ToDictionary(x => x.TagId, x => x.Value!.Value);
        var after = new FactorySimulator(asset, Day.AddHours(1), seed: 3).Step(Day.AddHours(1).AddSeconds(1)).ToDictionary(x => x.TagId, x => x.Value!.Value);
        Assert.True(after[energy.TagId] > before[energy.TagId]);
        Assert.True(after[parts.TagId] > before[parts.TagId]);
    }

    [Fact]
    public void Counters_only_go_up_and_values_stay_in_range()
    {
        var energy = Tag("energy", "energy_kwh", "counter_energy");
        var parts = Tag("parts", "part_count", "counter_parts");
        var load = Tag("load", "spindle_load", "load", 0, 100);
        var state = Tag("state", "machine_state", "state", 0, 5);
        var sim = new FactorySimulator(Asset("cnc", 22, energy, parts, load, state), Day, seed: 7);
        double lastEnergy = 0, lastParts = 0;
        for (var i = 1; i <= 7200; i++)   // two hours, one read per second
        {
            var v = sim.Step(Day.AddSeconds(i)).ToDictionary(x => x.TagId, x => x.Value!.Value);
            Assert.True(v[energy.TagId] >= lastEnergy);
            Assert.True(v[parts.TagId] >= lastParts);
            Assert.InRange(v[load.TagId], 0, 100);
            Assert.InRange(v[state.TagId], 0, 5);
            lastEnergy = v[energy.TagId];
            lastParts = v[parts.TagId];
        }
        Assert.True(lastParts > 0, "a busy CNC makes parts in two hours");
    }

    [Fact]
    public void Vibration_climbs_before_a_breakdown()
    {
        var vib = Tag("vib", "vibration_rms", "vibration", 0, 50);
        var state = Tag("state", "machine_state", "state");
        // search a few seeds for a run that breaks down within twelve simulated hours
        for (var seed = 1; seed < 60; seed++)
        {
            var sim = new FactorySimulator(Asset("cnc", 22, vib, state), Day, seed);
            var history = new List<(double Vib, double State)>();
            for (var i = 1; i <= 12 * 3600; i += 5)
            {
                var v = sim.Step(Day.AddSeconds(i)).ToDictionary(x => x.TagId, x => x.Value!.Value);
                history.Add((v[vib.TagId], v[state.TagId]));
                if (v[state.TagId] == MachineStates.Fault)
                {
                    var before = history.SkipLast(1).TakeLast(12).Average(h => h.Vib);    // last minute before the fault
                    var healthy = history.Where(h => h.State == MachineStates.Running).Take(60).Average(h => h.Vib);
                    Assert.True(before > healthy * 2, $"vibration before fault {before:F2} vs healthy {healthy:F2}");
                    return;
                }
            }
        }
        Assert.Fail("No breakdown happened in any simulated run.");
    }

    [Fact]
    public void Agv_drives_its_loop_and_recharges()
    {
        var battery = Tag("battery", "battery_soc", "battery", 0, 100);
        var x = Tag("x", "position_x", "position_x", 0, 200);
        var y = Tag("y", "position_y", "position_y", 0, 100);
        var sim = new FactorySimulator(Asset("agv", 3, battery, x, y), Day, seed: 3);
        var sawCharging = false;
        for (var i = 1; i <= 6 * 3600; i += 10)
        {
            var v = sim.Step(Day.AddSeconds(i)).ToDictionary(t => t.TagId, t => t.Value!.Value);
            Assert.InRange(v[battery.TagId], 0, 100);
            Assert.InRange(v[x.TagId], 20, 180);
            Assert.InRange(v[y.TagId], 20, 80);
            sawCharging |= sim.State == MachineStates.Idle;
        }
        Assert.True(sawCharging, "the AGV went to charge at least once in six hours");
    }

    [Fact]
    public void Factory_day_and_sun_follow_taiwan_time()
    {
        Assert.True(FactorySimulator.Busyness(Day) > FactorySimulator.Busyness(new DateTime(2026, 10, 16, 18, 0, 0, DateTimeKind.Utc)));   // 10:00 vs 02:00
        Assert.Equal(0, FactorySimulator.Solar(new DateTime(2026, 10, 16, 14, 0, 0, DateTimeKind.Utc)));   // 22:00 Taiwan
        Assert.True(FactorySimulator.Solar(new DateTime(2026, 10, 16, 4, 0, 0, DateTimeKind.Utc)) > 0.8);   // noon Taiwan
    }

    // ---------------------------------------------------------------- engine

    private sealed class FakeStore(params AssetBinding[] bindings) : IIngestionStore
    {
        public List<AssetBinding> Bindings { get; } = [.. bindings];
        public List<ReadingRow> Written { get; } = [];
        public List<AssetHealth> Health { get; } = [];

        public Task<IReadOnlyList<AssetBinding>> ActiveBindingsAsync(IReadOnlyCollection<string> protocols, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AssetBinding>>(Bindings);

        public Task WriteAsync(IReadOnlyList<ReadingRow> rows, CancellationToken ct)
        {
            Written.AddRange(rows);
            return Task.CompletedTask;
        }

        public Task UpdateHealthAsync(IReadOnlyList<AssetHealth> health, CancellationToken ct)
        {
            Health.AddRange(health);
            return Task.CompletedTask;
        }
    }

    /// <summary>A device whose values the test sets; it can be told to fail.</summary>
    private sealed class FakeConnector(AssetBinding asset, Func<TagBinding, double?> value, Func<bool> fail) : IConnector
    {
        public Task ConnectAsync(CancellationToken ct) => fail() ? throw new IOException("device unreachable") : Task.CompletedTask;

        public Task<IReadOnlyList<RawValue>> ReadAsync(DateTime now, CancellationToken ct) => fail()
            ? throw new IOException("device unreachable")
            : Task.FromResult<IReadOnlyList<RawValue>>(asset.Tags.Select(t => new RawValue(t.TagId, value(t), Quality.Good, now)).ToList());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory(Func<TagBinding, double?> value, Func<bool> fail) : IConnectorFactory
    {
        public IReadOnlyCollection<string> Protocols { get; } = ["simulator"];
        public IConnector Create(AssetBinding asset) => new FakeConnector(asset, value, fail);
    }

    [Fact]
    public async Task Engine_scales_values_stores_by_exception_and_sends_a_heartbeat()
    {
        var temp = new TagBinding(Guid.NewGuid(), "temp", "temperature", "temp", null, 0.1, -40, 0.5, -40, 200);   // raw tenths, offset -40
        var asset = Asset("sensor", null, temp);
        var store = new FakeStore(asset);
        double raw = 650;   // 650 × 0.1 − 40 = 25.0 °C
        var engine = new IngestionEngine(store, new ConnectorRegistry([new FakeFactory(_ => raw, () => false)]),
            new IngestionOptions { HeartbeatSeconds = 60 }, NullLogger<IngestionEngine>.Instance);

        await engine.RunOnceAsync(Day, default);
        Assert.Equal(25.0, store.Written.Single().Value!.Value, 6);
        Assert.True(store.Written.Single().Store);

        raw = 652;   // +0.2 °C, inside the 0.5 deadband: live value updates, history does not
        await engine.RunOnceAsync(Day.AddSeconds(1.1), default);
        Assert.False(store.Written.Last().Store);

        raw = 660;   // +1.0 °C: stored
        await engine.RunOnceAsync(Day.AddSeconds(2.2), default);
        Assert.True(store.Written.Last().Store);

        await engine.RunOnceAsync(Day.AddSeconds(70), default);   // unchanged but the heartbeat is due
        Assert.True(store.Written.Last().Store);
        Assert.Equal("Running", store.Health.Last().Status);
    }

    [Fact]
    public async Task Engine_marks_an_asset_offline_after_three_failed_reads_and_recovers()
    {
        var state = Tag("state", "machine_state", "state");
        var asset = Asset("cnc", 10, state);
        var store = new FakeStore(asset);
        var broken = true;
        var engine = new IngestionEngine(store, new ConnectorRegistry([new FakeFactory(_ => MachineStates.Running, () => broken)]),
            new IngestionOptions { FailuresBeforeOffline = 3 }, NullLogger<IngestionEngine>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await engine.RunOnceAsync(Day.AddSeconds(i * 2), default);
        }
        Assert.Equal(["Unchanged", "Unchanged", "Offline"], store.Health.Select(h => h.Status));
        Assert.Contains("unreachable", store.Health.Last().LastError);
        Assert.Equal(Quality.Offline, store.Written.Last().Quality);

        broken = false;
        await engine.RunOnceAsync(Day.AddSeconds(10), default);
        Assert.Equal("Running", store.Health.Last().Status);   // from the machine_state tag
        Assert.Null(store.Health.Last().LastError);
    }

    [Fact]
    public async Task Engine_flags_out_of_range_values_and_picks_up_new_tags()
    {
        var p = Tag("p", "pressure", "pressure", 0, 16);
        var asset = Asset("air-compressor", 50, p);
        var store = new FakeStore(asset);
        var engine = new IngestionEngine(store, new ConnectorRegistry([new FakeFactory(_ => 25, () => false)]),
            new IngestionOptions { RefreshBindingsSeconds = 5 }, NullLogger<IngestionEngine>.Instance);
        await engine.RunOnceAsync(Day, default);
        Assert.Equal(Quality.Uncertain, store.Written.Single().Quality);

        var extra = Tag("flow", "flow_air", "flow", 0, 60);
        store.Bindings[0] = asset with { Tags = [p, extra] };
        await engine.RunOnceAsync(Day.AddSeconds(6), default);
        Assert.Contains(store.Written, r => r.TagId == extra.TagId);
    }
}
