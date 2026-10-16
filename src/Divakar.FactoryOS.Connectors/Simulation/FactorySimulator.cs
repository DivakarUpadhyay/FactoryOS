using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.Connectors.Simulation;

/// <summary>
/// A believable model of one asset. The machine state moves between running, idle, fault and maintenance with
/// probabilities that follow the factory day (Taiwan time: night shift and lunch are quieter); every tag is derived
/// from that state: power follows load, energy and part counters integrate over time, temperatures approach their
/// targets gradually, vibration climbs during the minutes before a breakdown, AGV batteries drain and recharge.
/// Deterministic for a given asset and start time, so tests can rely on it.
/// </summary>
public sealed class FactorySimulator
{
    private readonly AssetBinding asset;
    private readonly Random rng;
    private readonly Dictionary<Guid, double> values = [];
    private readonly double ratedKw;
    private readonly double cycleSec;
    private DateTime last;
    private int state;
    private double stateSeconds;
    private double degradation;     // 0 = healthy, 1 = about to fail
    private bool failing;
    private double pathPos;         // AGV position along its loop, 0..1
    private double partsFraction;
    private int alarm;

    public FactorySimulator(AssetBinding asset, DateTime start, int? seed = null)
    {
        this.asset = asset;
        rng = new Random(seed ?? StableSeed(asset.AssetId));
        last = start;
        ratedKw = (double)(asset.RatedPowerKw ?? 10m);
        cycleSec = DefaultCycle(asset.AssetTypeCode);
        state = rng.NextDouble() < 0.75 ? MachineStates.Running : MachineStates.Idle;
        pathPos = rng.NextDouble();
        // counters start from where a long-running device would be at this moment, so they keep rising across worker restarts
        var hours = Math.Max(0, (start - CounterEpoch).TotalHours);
        foreach (var t in asset.Tags)
        {
            values[t.TagId] = Initial(t, hours);
        }
    }

    public int State => state;
    public double Degradation => degradation;

    /// <summary>Advances the model to <paramref name="now"/> and returns a value for every tag.</summary>
    public IReadOnlyList<RawValue> Step(DateTime now)
    {
        var dt = Math.Clamp((now - last).TotalSeconds, 0, 3600);
        last = now;
        AdvanceState(now, dt);
        var output = new List<RawValue>(asset.Tags.Count);
        foreach (var t in asset.Tags)
        {
            var v = Next(t, now, dt);
            values[t.TagId] = v;
            output.Add(new RawValue(t.TagId, Math.Round(v, 3), Quality.Good, now));
        }
        return output;
    }

    // ---------------------------------------------------------------- machine state

    private static readonly HashSet<string> NoStateTypes =
        ["energy-meter", "power-analyzer", "transformer", "ups", "solar-inverter", "battery-storage", "ev-charger", "water-meter",
         "gas-meter", "sensor", "camera", "thermal-camera", "rfid-reader", "scanner", "gas-detector", "safety-plc", "hvac", "plc", "gateway"];

    private void AdvanceState(DateTime now, double dt)
    {
        stateSeconds += dt;
        if (NoStateTypes.Contains(asset.AssetTypeCode))
        {
            state = MachineStates.Running;
            return;
        }
        if (asset.AssetTypeCode is "agv" or "amr")
        {
            AdvanceVehicle(dt);
            return;
        }
        var minutes = dt / 60.0;
        var busy = Busyness(now);
        switch (state)
        {
            case MachineStates.Running:
                if (!failing && Chance(0.004 * minutes))
                {
                    failing = true;   // wear starts: vibration and temperature creep up before the fault
                }
                if (failing)
                {
                    degradation = Math.Min(1, degradation + dt / 600.0);   // about ten minutes of warning
                    if (degradation >= 1)
                    {
                        Go(MachineStates.Fault);
                        alarm = 100 + rng.Next(900);
                    }
                }
                else if (Chance((1 - busy) * 0.25 * minutes + 0.01 * minutes))
                {
                    Go(MachineStates.Idle);
                }
                break;
            case MachineStates.Idle:
                if (Chance(busy * 0.5 * minutes))
                {
                    Go(MachineStates.Running);
                }
                break;
            case MachineStates.Fault:
                if (stateSeconds > 180 + rng.Next(420))
                {
                    Go(MachineStates.Maintenance);
                }
                break;
            case MachineStates.Maintenance:
                if (stateSeconds > 600 + rng.Next(600))
                {
                    failing = false;
                    degradation = 0;
                    alarm = 0;
                    Go(MachineStates.Idle);
                }
                break;
            default:
                Go(MachineStates.Idle);
                break;
        }
    }

    private void AdvanceVehicle(double dt)
    {
        var battery = asset.Tags.FirstOrDefault(t => t.Profile == "battery") is { } b ? values[b.TagId] : 80;
        if (state == MachineStates.Running && battery < 20)
        {
            Go(MachineStates.Idle);   // drives to the charger
        }
        else if (state != MachineStates.Running && battery > 95)
        {
            Go(MachineStates.Running);
        }
        if (state == MachineStates.Running)
        {
            pathPos = (pathPos + dt * 1.2 / 440.0) % 1.0;   // 1.2 m/s on a 440 m loop
        }
    }

    private void Go(int next)
    {
        state = next;
        stateSeconds = 0;
    }

    /// <summary>0..1, how busy the factory is at this time of day in Taiwan (UTC+8).</summary>
    public static double Busyness(DateTime utc)
    {
        var h = (utc.Hour + 8) % 24 + utc.Minute / 60.0;
        if (h is >= 12 and < 13)
        {
            return 0.35;   // lunch
        }
        if (h is >= 22 or < 6)
        {
            return 0.55;   // night shift
        }
        return 0.95;
    }

    private bool Chance(double p) => rng.NextDouble() < p;

    private double Noise(double amplitude) => (rng.NextDouble() * 2 - 1) * amplitude;

    // ---------------------------------------------------------------- tag values

    private double Load => state switch
    {
        MachineStates.Running => 0.62 + 0.25 * Math.Sin(stateSeconds / 37.0) * 0.4 + 0.15 * degradation,
        MachineStates.Idle => 0.12,
        MachineStates.Maintenance => 0.04,
        _ => 0.02,
    };

    private static readonly DateTime CounterEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private double Initial(TagBinding t, double hours) => t.Profile switch
    {
        "counter_energy" => 20_000 + rng.Next(80_000) + ratedKw * 0.45 * hours,
        "counter_volume" => 1_000 + rng.Next(9_000) + 2.5 * hours,
        "counter_parts" => rng.Next(5_000) + Math.Floor(hours * 3600 / cycleSec * 0.5),
        "counter_scrap" => rng.Next(80) + Math.Floor(hours * 3600 / cycleSec * 0.01),
        "battery" => 50 + rng.Next(45),
        "temp" => Lerp(t, 0.25),
        "level" => 60,
        _ => 0,
    };

    private double Next(TagBinding t, DateTime now, double dt)
    {
        var prev = values[t.TagId];
        var running = state == MachineStates.Running;
        var kw = PowerKw(now);
        switch (t.Profile)
        {
            case "state":
                return state;
            case "alarm":
                return state == MachineStates.Fault ? alarm : 0;
            case "counter_parts":
                if (running)
                {
                    partsFraction += dt / (cycleSec * (1 + 0.15 * degradation)) * (0.92 + Noise(0.05));
                }
                var whole = Math.Floor(partsFraction);
                partsFraction -= whole;
                return prev + whole;
            case "counter_scrap":
                return running && Chance(dt / cycleSec * (0.015 + 0.08 * degradation)) ? prev + 1 : prev;
            case "cycle":
                return running ? cycleSec * (1 + 0.15 * degradation) * (1 + Noise(0.04)) : prev == 0 ? cycleSec : prev;
            case "load":
                return Math.Clamp(Load * 100 + Noise(4), 0, 100);
            case "speed":
                return running ? Lerp(t, 0.55 + Noise(0.05)) : state == MachineStates.Idle ? Lerp(t, 0.05) : 0;
            case "temp":
                return Approach(prev, Lerp(t, running ? 0.35 + 0.25 * Load + 0.2 * degradation : 0.18), dt, 300) + Noise(0.15);
            case "vibration":
                var baseV = running ? 1.6 : state == MachineStates.Idle ? 0.4 : 0.05;
                return Math.Max(0, baseV * (1 + 3.5 * degradation * degradation) + Noise(0.12 * baseV + 0.02));
            case "power":
                return Math.Max(0, kw);
            case "reactive":
                return Math.Max(0, kw * Math.Tan(Math.Acos(PowerFactor())));
            case "pf":
                return PowerFactor();
            case "current":
                return Math.Max(0, kw * 1000 / (Math.Sqrt(3) * 380 * PowerFactor())) + Noise(0.3);
            case "voltage":
                return 220 + 3 * Math.Sin(now.TimeOfDay.TotalHours / 24 * 2 * Math.PI) + Noise(0.6);
            case "frequency":
                return 60 + Noise(0.02);
            case "thd":
                return t.SensorTypeCode == "thd_current" ? 9 + 10 * Load + Noise(1) : 2.5 + Noise(0.4);
            case "counter_energy":
                return prev + Math.Max(0, kw) * dt / 3600.0;
            case "flow":
                return Math.Max(0, Lerp(t, running ? 0.45 * Load / 0.6 : 0.03) + Noise(Lerp(t, 0.01)));
            case "counter_volume":
                var flowTag = asset.Tags.FirstOrDefault(x => x.Profile == "flow");
                var perHour = flowTag is null ? 5 : values[flowTag.TagId];
                return prev + perHour * dt / 3600.0;   // flow is per hour
            case "pressure":
                return running ? Lerp(t, 0.45) + Noise(0.08) : Approach(prev, Lerp(t, 0.1), dt, 60);
            case "level":
                return Math.Clamp(60 + 20 * Math.Sin(now.TimeOfDay.TotalHours / 6 * Math.PI) + Noise(0.5), 0, 100);
            case "ph":
                return 7.1 + 0.25 * Math.Sin(now.TimeOfDay.TotalHours) + Noise(0.03);
            case "weight":
                return running ? Lerp(t, 0.035) * (0.9 + Noise(0.08)) : 0;
            case "gas":
                return Math.Max(0, 2 + Noise(1.2) + (Chance(0.0005 * dt) ? 30 : 0));
            case "co2":
                return 450 + 25 * Occupancy(now) + Noise(15);
            case "bool_rare":
                return t.SensorTypeCode == "door_open" ? (state == MachineStates.Maintenance || Chance(0.002 * dt) ? 1 : 0) : 0;
            case "noise":
                return running ? 80 + Noise(2.5) : 55 + Noise(2);
            case "occupancy":
                return Occupancy(now);
            case "env_humidity":
                return 55 + 8 * Math.Sin(now.TimeOfDay.TotalHours / 24 * 2 * Math.PI) + Noise(0.8);
            case "env_slow":
                return Math.Max(0, Lerp(t, 0.2 + 0.05 * Math.Sin(now.TimeOfDay.TotalHours)) + Noise(Lerp(t, 0.005)));
            case "battery":
                var batteryRate = state == MachineStates.Running ? -0.03 : 0.18;   // % per second
                if (asset.AssetTypeCode == "ups")
                {
                    batteryRate = prev < 100 ? 0.01 : 0;
                }
                if (asset.AssetTypeCode == "battery-storage")
                {
                    batteryRate = Solar(now) > 0.3 ? 0.004 : -0.003;
                }
                return Math.Clamp(prev + batteryRate * dt, 0, 100);
            case "position_x":
                return Path().X;
            case "position_y":
                return Path().Y;
            case "solar":
                return Math.Max(0, 1000 * Solar(now) + Noise(20));
            case "solar_power":
                return Math.Max(0, ratedKw * Solar(now) * 0.86);
            default:
                return prev;
        }
    }

    private double PowerKw(DateTime now) => asset.AssetTypeCode switch
    {
        "solar-inverter" => ratedKw * Solar(now) * 0.83,
        "battery-storage" => (Solar(now) > 0.3 ? -0.4 : 0.3) * ratedKw,   // negative = charging
        "energy-meter" or "power-analyzer" => SiteLoadKw(now),
        "transformer" => SiteLoadKw(now) * 1.02,
        "ups" => ratedKw * 0.55,
        "ev-charger" => Busyness(now) < 0.6 ? ratedKw * 0.9 : ratedKw * 0.1,
        _ => ratedKw * Load + Noise(ratedKw * 0.02),
    };

    /// <summary>A main or sub meter follows the whole factory's day shape.</summary>
    private double SiteLoadKw(DateTime now)
    {
        var scale = asset.AssetCode.Contains("main", StringComparison.OrdinalIgnoreCase) ? 900 : 250;
        return scale * (0.35 + 0.6 * Busyness(now)) + Noise(scale * 0.03);
    }

    private double PowerFactor() => state == MachineStates.Running ? 0.9 + Noise(0.02) : 0.72 + Noise(0.03);

    /// <summary>0..1 solar strength for the hour in Taiwan, zero at night.</summary>
    public static double Solar(DateTime utc)
    {
        var h = (utc.Hour + 8) % 24 + utc.Minute / 60.0;
        return h is < 6 or > 18 ? 0 : Math.Max(0, Math.Sin((h - 6) / 12 * Math.PI)) * 0.9;
    }

    private static double Occupancy(DateTime utc) => Math.Round(30 * Busyness(utc));

    /// <summary>The AGV loop: a 160 m × 60 m rectangle.</summary>
    private (double X, double Y) Path()
    {
        var d = pathPos * 440;
        return d switch
        {
            < 160 => (20 + d, 20),
            < 220 => (180, 20 + (d - 160)),
            < 380 => (180 - (d - 220), 80),
            _ => (20, 80 - (d - 380)),
        };
    }

    private static double Approach(double current, double target, double dt, double tauSeconds) =>
        target + (current - target) * Math.Exp(-dt / tauSeconds);

    private static double Lerp(TagBinding t, double f)
    {
        var min = t.MinValue ?? 0;
        var max = t.MaxValue ?? (min + 100);
        return min + (max - min) * f;
    }

    private static double DefaultCycle(string type) => type switch
    {
        "cnc" => 90,
        "injection" => 38,
        "press" => 4,
        "smt" => 30,
        "robot-arm" or "cobot" => 40,
        "packaging" => 6,
        "camera" => 30,
        "agv" or "amr" => 300,
        "rfid-reader" or "scanner" => 20,
        _ => 30,
    };

    private static int StableSeed(Guid id)
    {
        var b = id.ToByteArray();
        return BitConverter.ToInt32(b, 0) ^ BitConverter.ToInt32(b, 12);
    }
}
