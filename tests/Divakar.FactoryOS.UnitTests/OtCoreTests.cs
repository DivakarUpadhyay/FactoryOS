using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Divakar.FactoryOS.Application.Ai;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Enms;
using Divakar.FactoryOS.Connectors.Drivers;
using Divakar.FactoryOS.Domain.Plant;
using Divakar.FactoryOS.Edge;

namespace Divakar.FactoryOS.UnitTests;

public sealed class MeasurementAndVerificationTests
{
    [Theory]
    [InlineData(0.975, 1.959964)]
    [InlineData(0.95, 1.644854)]
    [InlineData(0.84, 0.994458)]
    public void Normal_quantiles_match_the_tables(double p, double z) => Assert.Equal(z, MvMath.Normal(p), 5);

    [Theory]
    [InlineData(90, 10, 1.812)]
    [InlineData(95, 10, 2.228)]
    [InlineData(95, 30, 2.042)]
    [InlineData(90, 60, 1.671)]
    public void Student_t_matches_the_tables(double confidence, int dof, double t) => Assert.Equal(t, MvMath.StudentT(confidence, dof), 2);

    [Fact]
    public void A_perfect_model_has_no_error_and_no_bias()
    {
        var y = new double[] { 100, 120, 140, 160, 180 };
        var fit = MvMath.Fit(y, y, 2);
        Assert.Equal(0, fit.CvRmsePct);
        Assert.Equal(0, fit.NmbePct);
    }

    [Fact]
    public void CvRmse_and_nmbe_use_n_minus_p_degrees_of_freedom()
    {
        // residuals +10, −10, +10, −10 around a mean of 100, two parameters: RMSE = √(400 / 2) = 14.14, CV = 14.14 %
        var fit = MvMath.Fit([110, 90, 110, 90], [100, 100, 100, 100], 2);
        Assert.Equal(14.14, fit.CvRmsePct, 1);
        Assert.Equal(0, fit.NmbePct, 6);
    }

    [Fact]
    public void Positive_autocorrelation_shrinks_the_effective_sample()
    {
        var actual = Enumerable.Range(0, 30).Select(i => 100 + (i < 15 ? 5.0 : -5.0)).ToArray();   // long runs of same-sign residuals
        var fit = MvMath.Fit(actual, [.. Enumerable.Repeat(100.0, 30)], 1);
        Assert.True(fit.Autocorrelation > 0.5);
        Assert.True(fit.EffectiveN < 10);
    }

    [Fact]
    public void Longer_reporting_periods_give_smaller_fractional_savings_uncertainty()
    {
        var fit = new ModelFit(30, 2, 1000, 10, 0, 0, 30);
        var shortRun = MvMath.Savings(fit, 7 * 1000, 7 * 900, 7, 90, 0);
        var longRun = MvMath.Savings(fit, 60 * 1000, 60 * 900, 60, 90, 0);
        Assert.Equal(10, shortRun.SavingsFraction, 3);
        Assert.True(longRun.Fsu68Pct < shortRun.Fsu68Pct);
    }

    [Fact]
    public void Meter_uncertainty_adds_in_quadrature()
    {
        var fit = new ModelFit(30, 2, 1000, 10, 0, 0, 30);
        var without = MvMath.Savings(fit, 30_000, 27_000, 30, 90, 0);
        var with = MvMath.Savings(fit, 30_000, 27_000, 30, 90, 1);
        Assert.True(with.CombinedUncertaintyKwh > without.CombinedUncertaintyKwh);
        Assert.InRange(with.CombinedUncertaintyKwh - Math.Sqrt(Math.Pow(with.ModelUncertaintyKwh, 2) + Math.Pow(with.MeterUncertaintyKwh, 2)), -0.2, 0.2);
    }

    private static Meter Meter(int? interval, DateTime? last, string? result = "Pass", string kind = "Main") =>
        new(Guid.NewGuid(), "Main meter", null, null, "electricity", kind, null, null, "0.5S", 0.5m, interval, last, null, true, result);

    [Fact]
    public void Calibration_state_follows_the_interval()
    {
        var today = new DateTime(2026, 11, 7);
        Assert.Equal("ok", EnmsService.Status(Meter(24, today.AddMonths(-6)), today).State);
        Assert.Equal("due", EnmsService.Status(Meter(12, today.AddMonths(-12).AddDays(10)), today).State);
        Assert.Equal("overdue", EnmsService.Status(Meter(12, today.AddMonths(-13)), today).State);
        Assert.Equal("never", EnmsService.Status(Meter(12, null), today).State);
        Assert.Equal("untracked", EnmsService.Status(Meter(null, today), today).State);
        Assert.Equal("failed", EnmsService.Status(Meter(12, today.AddMonths(-1), "Fail"), today).State);
        Assert.Equal(new DateTime(2028, 5, 7), EnmsService.Status(Meter(24, today.AddMonths(-6)), today).NextDueOn);
    }

    [Fact]
    public void A_weak_model_fails_the_ASHRAE_checks()
    {
        var fit = new ModelFit(20, 2, 1000, 40, 12, 0.2, 13);
        var checks = EnmsService.Checks(fit, null, null);
        Assert.Equal("bad", checks.Single(c => c.Name == "Model CV(RMSE)").State);
        Assert.Equal("bad", checks.Single(c => c.Name == "Model bias NMBE").State);
    }
}

public sealed class GhgFactorTests
{
    [Fact]
    public void Blend_GWPs_are_the_mass_weighted_sum_of_their_components()
    {
        var f = EmissionMath.ActivityDefaults().ToDictionary(x => x.Source, x => x.KgCo2ePerUnit!.Value);
        Assert.Equal(1924, (double)f["ref_r410a"], 0);   // 50 % R-32 + 50 % R-125 = 1923.5
        Assert.Equal(0.5 * 677 + 0.5 * 3170, (double)f["ref_r410a"], 0);
        Assert.Equal(0.44 * 3170 + 0.52 * 4800 + 0.04 * 1300, (double)f["ref_r404a"], 0);
        Assert.Equal(0.23 * 677 + 0.25 * 3170 + 0.52 * 1300, (double)f["ref_r407c"], 0);
    }

    [Fact]
    public void Calcination_factors_are_the_molar_mass_ratios()
    {
        var f = EmissionMath.ActivityDefaults().ToDictionary(x => x.Source, x => (double)x.KgCo2ePerUnit!.Value);
        Assert.Equal(44.0095 / 100.0869, f["proc_calcite"], 4);
        Assert.Equal(2 * 44.0095 / 184.401, f["proc_dolomite"], 4);
    }

    [Fact]
    public void Every_default_factor_is_consistent_with_its_category()
    {
        foreach (var f in EmissionMath.ActivityDefaults())
        {
            Assert.Matches(EmissionMath.ActivityCode, f.Source);
            switch (EmissionMath.Category(f.Source))
            {
                case "refrigerant": Assert.Equal(("Fugitive", (byte)1), (f.Method, f.Scope)); break;
                case "process": Assert.Equal(("Process", (byte)1), (f.Method, f.Scope)); break;
                default: Assert.Equal(("Activity", (byte)3), (f.Method, f.Scope)); Assert.InRange(f.Scope3Category!.Value, 1, 15); break;
            }
            Assert.True(f.Unit.Length <= 10);
            Assert.False(string.IsNullOrWhiteSpace(f.Reference));
        }
    }

    [Fact]
    public void Other_regions_grid_factors_start_switched_off_so_Taiwan_stays_in_use()
    {
        Assert.All(EmissionMath.RegionalGridFactors(), f => Assert.False(f.IsActive));
        var lib = EmissionMath.TaiwanDefaults().Concat(EmissionMath.RegionalGridFactors()).Select(r => new EmissionFactor(Guid.NewGuid(), r.Source, r.Name, r.Scope,
            r.Method, r.Region, r.Year, r.Unit, r.HeatKcalPerUnit, r.Co2KgPerTj, r.Ch4KgPerTj, r.N2oKgPerTj, r.KgCo2ePerUnit, r.GwpSet ?? "AR5", r.Reference, r.IsActive)).ToList();
        Assert.Equal("TW", EmissionMath.GridRegion(lib));
        Assert.Equal(0.466, (double)EmissionMath.Resolve(lib, "electricity", "Location", 2026)!.Co2e, 3);
        var uk = lib.Select(f => f.Source == "electricity" ? f with { IsActive = f.Region == "UK" } : f).ToList();
        Assert.Equal("UK", EmissionMath.GridRegion(uk));
        Assert.Equal(0.177, (double)EmissionMath.Resolve(uk, "electricity", "Location", 2026)!.Co2e, 3);
    }

    [Fact]
    public void US_factor_is_the_published_lb_per_MWh_converted()
    {
        var us = EmissionMath.RegionalGridFactors().Single(f => f.Region == "US");
        Assert.Equal(770.9 * 0.45359237 / 1000, (double)us.KgCo2ePerUnit!.Value, 5);
    }

    [Theory]
    [InlineData("ref_r134a", "refrigerant")]
    [InlineData("proc_calcite", "process")]
    [InlineData("s3_freight_hgv", "scope3")]
    [InlineData("diesel", "energy")]
    public void Category_comes_from_the_code(string source, string category) => Assert.Equal(category, EmissionMath.Category(source));
}

public sealed class OtModelTests
{
    [Fact]
    public void Robust_statistics_ignore_an_outlier()
    {
        var xs = new double[] { 10, 11, 9, 10, 10, 11, 9, 1000 };
        Assert.Equal(10, OtModels.Median(xs));
        Assert.InRange(OtModels.RobustSigma(xs), 0.5, 2);
    }

    [Fact]
    public void A_frozen_signal_is_detected_only_when_it_normally_moves()
    {
        Assert.True(OtModels.IsFrozen([.. Enumerable.Repeat(42.0, 12)], 1.5));
        Assert.False(OtModels.IsFrozen([.. Enumerable.Repeat(42.0, 12)], 0));
        Assert.False(OtModels.IsFrozen([.. Enumerable.Range(0, 12).Select(i => 42.0 + i * 0.1)], 1.5));
    }

    [Fact]
    public void Time_to_limit_extrapolates_a_real_rise()
    {
        // rises 0.5 per hour from 50; limit 80 → 60 h after x = 0 at value 50, so 40 h from x = 20 (value 60)
        var pts = Enumerable.Range(0, 21).Select(i => ((double)i, 50 + 0.5 * i + (i % 2 == 0 ? 0.05 : -0.05))).ToList();
        Assert.Equal(40, OtModels.HoursToLimit(pts, 80, 20)!.Value, 0);
        var flat = Enumerable.Range(0, 21).Select(i => ((double)i, 50 + (i % 2 == 0 ? 1.0 : -1.0))).ToList();
        Assert.Null(OtModels.HoursToLimit(flat, 80, 20));
    }

    [Fact]
    public void Feedback_moves_sensitivity_within_bounds()
    {
        var s = 1.0;
        for (var i = 0; i < 20; i++)
        {
            s = OtModels.AfterFeedback(s, real: false);
        }
        Assert.Equal(3, s);
        for (var i = 0; i < 100; i++)
        {
            s = OtModels.AfterFeedback(s, real: true);
        }
        Assert.Equal(0.7, s, 6);
    }

    [Fact]
    public void Online_detector_finds_spikes_and_frozen_values_but_not_noise()
    {
        var d = new OnlineDetector();
        var rng = new Random(7);
        var found = new List<OnlineDetector.Finding>();
        for (var i = 0; i < 500; i++)
        {
            found.AddRange(d.Add(20 + rng.NextDouble() - 0.5));
        }
        Assert.DoesNotContain(found, f => f.Kind == "Spike");
        Assert.Contains(d.Add(35), f => f.Kind == "Spike");
        var frozen = Enumerable.Range(0, 40).SelectMany(_ => d.Add(20.123)).ToList();
        Assert.Contains(frozen, f => f.Kind == "Frozen");
    }

    private static readonly Guid Tenant = Guid.NewGuid(), Asset = Guid.NewGuid(), Tag = Guid.NewGuid();

    /// <summary>Four weeks of a meter using 20 kWh an hour in the day shift (8–20) and 4 at night, with a little noise.</summary>
    private static List<EnergyHour> Weeks(DateTime now, Func<DateTime, double, double>? change = null)
    {
        var rng = new Random(3);
        var rows = new List<EnergyHour>();
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-28);
        for (var h = start; h < now.AddHours(-1).AddMinutes(1); h = h.AddHours(1))
        {
            var v = (h.Hour is >= 8 and < 20 ? 20 : 4) + rng.NextDouble() - 0.5;
            rows.Add(new EnergyHour(Tenant, Asset, "Compressor", Tag, h, change?.Invoke(h, v) ?? v));
        }
        return rows;
    }

    [Fact]
    public void Normal_energy_raises_nothing()
    {
        var now = new DateTime(2026, 11, 4, 3, 10, 0, DateTimeKind.Utc);
        Assert.Empty(OtAiJob.EnergyFindings(Weeks(now), now, (_, _) => 1));
    }

    [Fact]
    public void Night_consumption_at_day_level_is_an_energy_anomaly()
    {
        var now = new DateTime(2026, 11, 4, 3, 10, 0, DateTimeKind.Utc);
        var rows = Weeks(now, (h, v) => h >= now.AddHours(-3) ? 20 : v);   // left running all night
        var f = Assert.Single(OtAiJob.EnergyFindings(rows, now, (_, _) => 1));
        Assert.Equal("EnergyAnomaly", f.Kind);
        Assert.Equal("Critical", f.Severity);
    }

    [Fact]
    public void False_alarm_feedback_raises_the_bar_for_a_small_rise()
    {
        var now = new DateTime(2026, 11, 4, 3, 10, 0, DateTimeKind.Utc);
        var rows = Weeks(now, (h, v) => h >= now.AddHours(-3) ? v + 3 : v);   // 7 kWh instead of 4 at night
        var found = Assert.Single(OtAiJob.EnergyFindings(rows, now, (_, _) => 1));
        Assert.InRange(found.Score, OtAiJob.EnergyZ, 3 * OtAiJob.EnergyZ);
        Assert.Empty(OtAiJob.EnergyFindings(rows, now, (_, _) => 3));
    }

    [Fact]
    public void A_meter_that_stops_counting_in_the_day_shift_is_flagged()
    {
        var now = new DateTime(2026, 11, 4, 14, 10, 0, DateTimeKind.Utc);
        var rows = Weeks(now, (h, v) => h >= now.AddHours(-4) ? 0 : v);
        Assert.Contains(OtAiJob.EnergyFindings(rows, now, (_, _) => 1), f => f.Kind == "MeterFlat");
    }

    [Fact]
    public void A_rising_bearing_temperature_drifts_and_gets_a_time_to_limit()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var rng = new Random(5);
        var hours = new List<AnalogHour>();
        for (var h = now.AddDays(-7); h < now.AddHours(-2); h = h.AddHours(1))
        {
            // steady at 60 °C until 48 h ago, then rising 0.4 °C per hour
            var age = (now - h).TotalHours;
            var v = 60 + (age < 48 ? (48 - age) * 0.4 : 0) + rng.NextDouble() * 0.2;
            hours.Add(new AnalogHour(Tenant, Asset, "Spindle", Tag, "Bearing temperature", "bearing_temp", "°C", 130, 95, h, v));
        }
        var recent = Enumerable.Range(0, 24).Select(i => new RecentValue(Tag, now.AddMinutes(-60 + i * 2.5), 60 + 48 * 0.4 + i * 0.02)).ToLookup(r => r.TagId);
        var found = OtAiJob.AnalogFindings(hours, recent, now, (_, _) => 1).ToList();
        Assert.Contains(found, f => f.Kind == "Drift");
        var ttl = Assert.Single(found, f => f.Kind == "TimeToLimit");
        Assert.InRange(ttl.Score, 20, 60);   // ~79 °C now, 95 °C alarm limit, +0.4 °C/h → ~40 h
    }

    [Fact]
    public void A_normal_daily_temperature_cycle_is_not_drift()
    {
        var now = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);   // night: the coolest hours
        double Temp(DateTime t) => 26 + 4 * Math.Sin((t.Hour - 9) / 24.0 * 2 * Math.PI);
        var hours = new List<AnalogHour>();
        for (var h = now.AddDays(-7); h < now.AddHours(-2); h = h.AddHours(1))
        {
            hours.Add(new AnalogHour(Tenant, Asset, "Hall sensor", Tag, "Temperature", "temperature", "°C", 60, null, h, Temp(h)));
        }
        var recent = Enumerable.Range(0, 24).Select(i => now.AddMinutes(-60 + i * 2.5)).Select(t => new RecentValue(Tag, t, Temp(t))).ToLookup(r => r.TagId);
        Assert.DoesNotContain(OtAiJob.AnalogFindings(hours, recent, now, (_, _) => 1), f => f.Kind == "Drift");
    }

    [Fact]
    public void A_machine_switching_between_running_and_idle_is_not_drift()
    {
        // spindle bearing at 70 °C while running, 40 °C while idle; it is running now, at its normal running temperature
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var buckets = new List<AnalogHour>();
        for (var b = now.AddDays(-7); b < now.AddHours(-2); b = b.AddMinutes(15))
        {
            var running = b.Hour is >= 8 and < 20;
            buckets.Add(new AnalogHour(Tenant, Asset, "CNC 1", Tag, "Bearing temperature", "bearing_temp", "°C", 130, 95, b, running ? 70 + (b.Minute % 30) * 0.01 : 40, running ? 2 : 1));
        }
        var recent = Enumerable.Range(0, 24).Select(i => new RecentValue(Tag, now.AddMinutes(-60 + i * 2.5), 70.1 + i % 3 * 0.05)).ToLookup(r => r.TagId);
        var states = new Dictionary<Guid, AssetState> { [Asset] = new(Asset, 2, now.AddHours(-4)) };
        Assert.DoesNotContain(OtAiJob.AnalogFindings(buckets, recent, now, (_, _) => 1, states), f => f.Kind == "Drift");
        // without the state, the same data looks like a big upward drift against the all-day normal
        var hot = Enumerable.Range(0, 24).Select(i => new RecentValue(Tag, now.AddMinutes(-60 + i * 2.5), 85 + i % 3 * 0.05)).ToLookup(r => r.TagId);
        Assert.Contains(OtAiJob.AnalogFindings(buckets, hot, now, (_, _) => 1, states), f => f.Kind == "Drift");
        // and a machine that changed state ten minutes ago is not judged yet
        states[Asset] = new(Asset, 2, now.AddMinutes(-10));
        Assert.Empty(OtAiJob.AnalogFindings(buckets, hot, now, (_, _) => 1, states));
    }

    [Fact]
    public void A_machine_idling_at_high_power_is_waste()
    {
        var now = DateTime.UtcNow;
        var rows = new[] { new IdleCandidate(Tenant, Asset, "Injection 2", 1, now.AddMinutes(-45), 18, 60m) };
        var f = Assert.Single(OtAiJob.IdleFindings(rows, now, (_, _) => 1));
        Assert.Equal("IdleWaste", f.Kind);
        Assert.Empty(OtAiJob.IdleFindings([rows[0] with { IdleSince = now.AddMinutes(-5) }], now, (_, _) => 1));
        Assert.Empty(OtAiJob.IdleFindings([rows[0] with { PowerKw = 2 }], now, (_, _) => 1));
    }
}

public sealed class ProtocolDriverTests
{
    private static AssetBinding Binding(string protocol, string endpoint, params (string Code, string Address)[] tags) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "A1", "cnc", protocol, endpoint, null, 200, null,
            [.. tags.Select(t => new TagBinding(Guid.NewGuid(), t.Code, "temperature", "temp", t.Address, 1, 0, 0, null, null))]);

    [Fact]
    public void Sparkplug_payload_round_trips_every_value_type()
    {
        var p = new SpPayload(1_700_000_000_000, 7,
        [
            new SpMetric("Temp", 1, null, SparkplugCodec.Double, false, 21.5),
            new SpMetric("Count", 2, null, SparkplugCodec.Int32, false, -42),
            new SpMetric("Speed", 3, null, SparkplugCodec.Float, false, 1.25),
            new SpMetric("Running", 4, null, SparkplugCodec.Boolean, false, 1),
            new SpMetric("Total", 5, null, SparkplugCodec.UInt64, false, 123456789012),
            new SpMetric("Missing", 6, null, SparkplugCodec.Double, true, null),
        ]);
        var back = SparkplugCodec.Decode(SparkplugCodec.Encode(p));
        Assert.Equal(7UL, back.Seq);
        Assert.Equal([21.5, -42, 1.25, 1, 123456789012, null], back.Metrics.Select(m => m.Value).ToArray());
        Assert.True(back.Metrics[5].IsNull);
    }

    [Fact]
    public void Sparkplug_host_resolves_aliases_from_birth_and_asks_for_rebirth_when_it_cannot()
    {
        var b = Binding("sparkplugb", "broker:1883", ("t", "Plant1/Line1/Oven|Zone1/Temp"));
        var c = new SparkplugConnector(b);
        var now = DateTime.UtcNow;
        var ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // data before any birth: unknown alias → rebirth request for the node
        var data = SparkplugCodec.Encode(new SpPayload(ts, 1, [new SpMetric(null, 10, null, SparkplugCodec.Double, false, 180)]));
        Assert.Equal(("Plant1", "Line1"), c.Handle("spBv1.0/Plant1/DDATA/Line1/Oven", data, now));
        Assert.Null(c.Handle("spBv1.0/Plant1/DDATA/Line1/Oven", data, now));   // throttled
        Assert.Equal(1, c.RebirthRequests);

        var birth = SparkplugCodec.Encode(new SpPayload(ts, 0, [new SpMetric("Zone1/Temp", 10, null, SparkplugCodec.Double, false, 175)]));
        c.Handle("spBv1.0/Plant1/DBIRTH/Line1/Oven", birth, now);
        c.Handle("spBv1.0/Plant1/DDATA/Line1/Oven", data, now);
        var v = Assert.Single(c.Snapshot(now));
        Assert.Equal(180, v.Value);
        Assert.Equal(Quality.Good, v.Quality);

        c.Handle("spBv1.0/Plant1/NDEATH/Line1", [], now.AddSeconds(1));
        Assert.Equal(Quality.Offline, Assert.Single(c.Snapshot(now.AddSeconds(2))).Quality);
        var rebirth = SparkplugCodec.Decode(System.Buffers.BuffersExtensions.ToArray(SparkplugConnector.RebirthMessage("Plant1", "Line1").Payload));
        Assert.Equal("Node Control/Rebirth", rebirth.Metrics[0].Name);
    }

    [Theory]
    [InlineData("/battery_state|sensor_msgs/msg/BatteryState|$.percentage", "/battery_state", "sensor_msgs/msg/BatteryState", "$.percentage")]
    [InlineData("/odom|$.twist.twist.linear.x", "/odom", null, "$.twist.twist.linear.x")]
    [InlineData("/temperature", "/temperature", null, "$.data")]
    public void Ros2_addresses_parse(string address, string topic, string? type, string path) =>
        Assert.Equal(new Ros2Address(topic, type, path), Ros2Address.Parse(address));

    [Fact]
    public void Ros2_reads_the_number_out_of_a_rosbridge_publish()
    {
        var b = Binding("ros2", "ws://robot:9090", ("soc", "/battery_state|sensor_msgs/msg/BatteryState|$.percentage"),
            ("vx", "/odom|nav_msgs/msg/Odometry|$.twist.twist.linear.x"));
        var c = new Ros2Connector(b);
        var now = DateTime.UtcNow;
        c.Handle("""{"op":"publish","topic":"/battery_state","msg":{"percentage":0.83,"voltage":24.1}}""", now);
        c.Handle("""{"op":"publish","topic":"/odom","msg":{"twist":{"twist":{"linear":{"x":0.42}}}}}""", now);
        c.Handle("""{"op":"status","level":"error","msg":"ignored"}""", now);
        Assert.Equal([0.83, 0.42], c.Snapshot(now).OrderBy(v => v.TagId == b.Tags[0].TagId ? 0 : 1).Select(v => v.Value!.Value).ToArray());
    }

    [Fact]
    public void Secs_items_round_trip()
    {
        var item = SecsItem.List(SecsItem.U4Of(1001), SecsItem.Ascii("12.5"), SecsItem.F8Of(3.25), SecsItem.Binary(0),
            SecsItem.Raw(SecsItem.I2, [0xFF, 0xFE]), SecsItem.List());
        var pos = 0;
        var back = SecsItem.Read(item.ToBytes(), ref pos);
        Assert.Equal(6, back.Items.Count);
        Assert.Equal([1001, 12.5, 3.25, 0, -2], back.Items.Take(5).Select(i => i.ToDouble()!.Value).ToArray());
        Assert.Equal(0xB1, SecsItem.U4Of(1).ToBytes()[0]);   // format byte of U4 with one length byte, as SEMI E5 shows it
        Assert.Equal(0x01, SecsItem.List().ToBytes()[0]);
    }

    /// <summary>A minimal piece of equipment: answers select, S1F13, S1F3 (SVID 1001 = 72.5 °C, 1002 = 3 wafers), link test.</summary>
    private static async Task FakeEquipment(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var s = client.GetStream();
        while (!ct.IsCancellationRequested)
        {
            HsmsMessage m;
            try
            {
                m = await HsmsMessage.ReadAsync(s, ct);
            }
            catch (Exception)
            {
                return;
            }
            HsmsMessage? reply = (m.SType, m.Stream, m.Function) switch
            {
                (HsmsMessage.SelectReq, _, _) => m with { SType = HsmsMessage.SelectRsp, Function = 0 },
                (HsmsMessage.LinktestReq, _, _) => m with { SType = HsmsMessage.LinktestRsp },
                (HsmsMessage.Data, 1, 13) => m with { Function = 14, WaitBit = false, Body = SecsItem.List(SecsItem.Binary(0), SecsItem.List()) },
                (HsmsMessage.Data, 1, 3) => m with
                {
                    Function = 4, WaitBit = false,
                    Body = SecsItem.List([.. m.Body!.Items.Select(i => BinaryPrimitives.ReadUInt32BigEndian(i.Data) switch
                    {
                        1001 => SecsItem.F8Of(72.5), 1002 => SecsItem.U4Of(3), _ => SecsItem.Raw(SecsItem.U4, []),
                    })]),
                },
                _ => null,
            };
            if (reply is not null)
            {
                await s.WriteAsync(reply.ToBytes(), ct);
            }
        }
    }

    [Fact]
    public async Task Secs_gem_host_selects_establishes_communication_and_reads_status_variables()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var equipment = FakeEquipment(listener, cts.Token);
        var b = Binding("secs-gem", $"127.0.0.1:{port}", ("temp", "1001"), ("wafers", "svid:1002"), ("unknown", "9999"));
        await using (var c = new SecsGemConnector(b))
        {
            await c.ConnectAsync(cts.Token);
            var values = await c.ReadAsync(DateTime.UtcNow, cts.Token);
            Assert.Equal(72.5, values.Single(v => v.TagId == b.Tags[0].TagId).Value);
            Assert.Equal(3, values.Single(v => v.TagId == b.Tags[1].TagId).Value);
            Assert.Equal(Quality.Bad, values.Single(v => v.TagId == b.Tags[2].TagId).Quality);
        }
        cts.Cancel();
        listener.Stop();
        await Task.WhenAny(equipment, Task.Delay(2000));
    }

    /// <summary>A minimal UR controller: RTDE v2, one recipe with robot_mode (INT32), actual_TCP_speed (VECTOR6D), streams data packages.</summary>
    private static async Task FakeRobot(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var s = client.GetStream();
        async Task<(byte, byte[])> Read()
        {
            var h = new byte[3];
            await s.ReadExactlyAsync(h, ct);
            var p = new byte[BinaryPrimitives.ReadUInt16BigEndian(h) - 3];
            await s.ReadExactlyAsync(p, ct);
            return (h[2], p);
        }
        var (t1, _) = await Read();
        Assert.Equal(UrRtdeConnector.RequestProtocolVersion, t1);
        await s.WriteAsync(UrRtdeConnector.Package(UrRtdeConnector.RequestProtocolVersion, [1]), ct);
        var (t2, setup) = await Read();
        Assert.Equal(UrRtdeConnector.ControlPackageSetupOutputs, t2);
        Assert.Equal("robot_mode,actual_TCP_speed", Encoding.ASCII.GetString(setup, 8, setup.Length - 8));
        await s.WriteAsync(UrRtdeConnector.Package(UrRtdeConnector.ControlPackageSetupOutputs, [1, .. Encoding.ASCII.GetBytes("INT32,VECTOR6D")]), ct);
        await Read();
        await s.WriteAsync(UrRtdeConnector.Package(UrRtdeConnector.ControlPackageStart, [1]), ct);
        var data = new byte[1 + 4 + 48];
        data[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(1), 7);   // ROBOT_MODE_RUNNING
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(5 + i * 8), 0.1 * (i + 1));
        }
        while (!ct.IsCancellationRequested)
        {
            await s.WriteAsync(UrRtdeConnector.Package(UrRtdeConnector.DataPackage, data), ct);
            await Task.Delay(20, ct);
        }
    }

    [Fact]
    public async Task Ur_rtde_negotiates_a_recipe_and_reads_vector_elements()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var robot = FakeRobot(listener, cts.Token);
        var b = Binding("ur-rtde", $"127.0.0.1:{port}", ("mode", "robot_mode"), ("vz", "actual_TCP_speed[2]"));
        await using (var c = new UrRtdeConnector(b))
        {
            await c.ConnectAsync(cts.Token);
            IReadOnlyList<RawValue> values = [];
            for (var i = 0; i < 100 && values.Count < 2; i++)
            {
                await Task.Delay(20);
                values = await c.ReadAsync(DateTime.UtcNow, cts.Token);
            }
            Assert.Equal(7, values.Single(v => v.TagId == b.Tags[0].TagId).Value);
            Assert.Equal(0.3, values.Single(v => v.TagId == b.Tags[1].TagId).Value!.Value, 6);
        }
        cts.Cancel();
        listener.Stop();
        await Task.WhenAny(robot, Task.Delay(2000));
    }
}

public sealed class EdgeAgentTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "fos-edge-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private static EdgeReading R(int i) => new(Guid.NewGuid(), DateTime.UtcNow, i, Quality.Good, true);

    [Fact]
    public async Task Buffer_forwards_everything_once_even_when_an_upload_fails_halfway()
    {
        var buffer = new DiskBuffer(dir);
        buffer.Append(Enumerable.Range(0, 250).Select(R));
        buffer.Roll();
        Assert.Equal(250, buffer.Pending());
        var received = new List<EdgeReading>();
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => buffer.DrainOneAsync(batch =>
        {
            if (++calls == 2)
            {
                throw new HttpRequestException("network down");
            }
            received.AddRange(batch);
            return Task.CompletedTask;
        }, 100, default));
        Assert.Equal(100, received.Count);
        Assert.Equal(150, buffer.Pending());
        // a restart in between: a new buffer on the same folder resumes where the last one stopped
        var again = new DiskBuffer(dir);
        Assert.Equal(150, await again.DrainOneAsync(batch => { received.AddRange(batch); return Task.CompletedTask; }, 100, default));
        Assert.Equal(Enumerable.Range(0, 250).Select(i => (double?)i), received.Select(r => r.Value));
        Assert.Equal(0, again.Pending());
    }

    [Fact]
    public void Buffer_drops_the_oldest_readings_when_full()
    {
        var buffer = new DiskBuffer(dir, maxBytes: 20_000, rollBytes: 5_000);
        for (var i = 0; i < 20; i++)
        {
            buffer.Append(Enumerable.Range(0, 50).Select(R));
        }
        buffer.Roll();
        Assert.True(buffer.Dropped > 0);
        Assert.True(Directory.GetFiles(dir, "*.seg").Sum(f => new FileInfo(f).Length) <= 20_000 + 5_000);
    }

    [Fact]
    public void Cached_configuration_is_encrypted_and_needs_the_agent_key()
    {
        var path = Path.Combine(dir, "config.bin");
        var config = new EdgeConfig(Guid.NewGuid(), "Line 1 gateway", DateTime.UtcNow,
            [new AssetBinding(Guid.NewGuid(), Guid.NewGuid(), "PLC1", "cnc", "modbus-tcp", "10.0.0.5:502", """{"password":"s3cret"}""", 1000, null, [])]);
        new ConfigCache(path, "fos_edge_key1").Save(config);
        Assert.DoesNotContain("s3cret", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("""{"password":"s3cret"}""", new ConfigCache(path, "fos_edge_key1").Load()!.Assets[0].Settings);
        Assert.Null(new ConfigCache(path, "fos_edge_other").Load());
    }
}
