namespace Aspire.Hosting.FordConnect.Emulator;

/// <summary>
/// The telemetry payload, in Ford's shapes.
///
/// The signal names and envelopes here were read off a real 2022 Mustang Mach-E in
/// August 2026, not invented, because the traps are all in the shapes:
///
///   - <c>batteryStateOfCharge</c> is the 12 V starter battery. It reads 64 % while the
///     car is nearly full, so a client that shows it as "your charge" is wrong in a way
///     nobody would question. The traction battery is <c>xevBatteryStateOfCharge</c>,
///     and both are emitted here so that mistake is available to make locally.
///   - <c>xevBatteryActualStateOfCharge</c> is not a fresher version of it — it is the
///     raw cell figure on its own schedule, measured months stale next to a display
///     value from minutes ago. It is deliberately given an ancient timestamp.
///   - <c>doorStatus</c>, <c>windowStatus</c>, <c>tirePressure</c>, <c>doorLockStatus</c>,
///     <c>seatBeltStatus</c> and the tyre-status signals come back as LISTS — one entry
///     per door, window or wheel — with no top-level <c>value</c> at all. A renderer
///     that assumes the <c>{value, updateTime}</c> envelope drops them silently, which
///     is exactly what happened to us.
///   - Almost every signal carries an <c>oemCorrelationId</c>, and some carry extra
///     keys of their own (<c>vehicleBattery</c>, <c>gpsModuleTimestamp</c>,
///     <c>tripProgress</c>, <c>parkingBrakeType</c>).
///   - The envelope's <c>updateTime</c> is NOT the newest stamp inside it, and it can
///     move on its own. Measured against the production log 2026-08-17: across 204
///     telemetry reads the envelope took 11 distinct values against 10 distinct
///     newest-metric stamps, it sat ahead of every metric inside it in 8 of 12
///     readings, and in exactly 1 of those 12 it had moved with no metric moving at
///     all. So a client deduplicating on the envelope records roughly one measurement
///     in twelve that the car never took — rare enough to survive review, frequent
///     enough to be a lie in the log.
///
///     Worth stating what this measurement *disproves*, because it was the earlier
///     assumption written into this file: the envelope does not move on every read.
///     If it did, 204 reads could not have produced 12 readings through a dedupe that
///     keys on it.
///
/// Position is emitted. The consuming platform decided in August 2026 to keep it,
/// and an emulator that quietly omits it would let position-handling code go untested
/// until it met the real car.
/// </summary>
public static class FordConnectTelemetry
{
    public const string Odometer = "odometer";
    public const string IgnitionSignal = "ignitionStatus";
    public const string StateOfChargeSignal = "xevBatteryStateOfCharge";

    /// <summary>Signals a moving car refreshes.</summary>
    public static readonly string[] DrivingSignals =
    [
        Odometer, IgnitionSignal, "speed", "position", "heading", "compassDirection",
        "gearLeverPosition", "acceleratorPedalPosition", "brakePedalStatus", "brakeTorque",
        "wheelTorqueStatus", "torqueAtTransmission", "yawRate", "outsideTemperature",
        "ambientTemp", StateOfChargeSignal, "xevBatteryRange", "xevBatteryEnergyRemaining",
        "batteryLoadStatus", "parkingBrakeStatus", "vehicleLifeCycleMode",
    ];

    /// <summary>Signals a charging car refreshes.</summary>
    public static readonly string[] ChargingSignals =
    [
        StateOfChargeSignal, "xevBatteryRange", "xevBatteryEnergyRemaining",
        "xevPlugChargerStatus", "xevBatteryChargeDisplayStatus", "xevBatteryTimeToFullCharge",
        "xevChargeStationPowerType", "xevChargeStationCommunicationStatus",
        "xevBatteryChargerCurrentOutput", "xevBatteryChargerVoltageOutput",
        "xevBatteryChargerEnergyOutput", "xevBatteryIoCurrent", "xevBatteryVoltage",
        "xevBatteryTemperature", "xevBatteryPerformanceStatus", "batteryVoltage",
    ];

    /// <summary>Signals whose real timestamps were days or weeks old. Ageing these on
    /// purpose is the point: "one read, one timestamp" is a false assumption and this is
    /// where it should break.</summary>
    public static readonly string[] SlowSignals =
    [
        "configurations", "oilLifeRemaining", "xevBatteryActualStateOfCharge",
        "xevBatteryCapacity", "xevBatteryMaximumRange", "displaySystemOfMeasure",
        "tirePressure", "tirePressureStatus", "tirePressureSystemStatus",
    ];

    /// <summary>Every signal the emulator emits, in the order Ford happens to send
    /// them (alphabetical, which is itself worth not relying on).</summary>
    public static readonly string[] SignalNames =
    [
        "acceleratorPedalPosition", "alarmStatus", "ambientTemp", "batteryLoadStatus",
        "batteryStateOfCharge", "batteryVoltage", "brakePedalStatus", "brakeTorque",
        "compassDirection", "configurations", "displaySystemOfMeasure", "doorLockStatus",
        "doorStatus", "engineCoolantTemp", "engineSpeed", "gearLeverPosition", "heading",
        "hoodStatus", "hybridVehicleModeStatus", IgnitionSignal, Odometer,
        "oilLifeRemaining", "outsideTemperature", "panicAlarmStatus", "parkingBrakeStatus",
        "position", "remoteStartCountdownTimer", "seatBeltStatus", "speed", "tirePressure",
        "tirePressureStatus", "tirePressureSystemStatus", "torqueAtTransmission",
        "tripFuelEconomy", "tripXevBatteryChargeRegenerated", "tripXevBatteryRangeRegenerated",
        "vehicleLifeCycleMode", "wheelTorqueStatus", "windowStatus",
        "xevBatteryActualStateOfCharge", "xevBatteryCapacity", "xevBatteryChargeDisplayStatus",
        "xevBatteryChargerCurrentOutput", "xevBatteryChargerEnergyOutput",
        "xevBatteryChargerVoltageOutput", "xevBatteryEnergyRemaining", "xevBatteryIoCurrent",
        "xevBatteryMaximumRange", "xevBatteryPerformanceStatus", "xevBatteryRange",
        StateOfChargeSignal, "xevBatteryTemperature", "xevBatteryTimeToFullCharge",
        "xevBatteryVoltage", "xevChargeStationCommunicationStatus", "xevChargeStationPowerType",
        "xevPlugChargerStatus", "xevTractionMotorCurrent", "xevTractionMotorVoltage", "yawRate",
    ];

    public static object Build(FordConnectCar car)
    {
        var stamps = car.Stamps;
        var charging = car.ChargeStatus is "ChargingAC" or "ChargingDC";
        var driving = car.Ignition == "ON";
        var metrics = new Dictionary<string, object?>(StringComparer.Ordinal);
        var correlation = 31_700;

        // A plain {value, updateTime, oemCorrelationId} signal, plus any extra keys the
        // real one carries.
        void Signal(string name, object? value, params (string Key, object? Value)[] extra)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["updateTime"] = Stamp(stamps, name),
                ["oemCorrelationId"] = (++correlation).ToString(),
            };
            foreach (var (key, extraValue) in extra) row[key] = extraValue;
            metrics[name] = row;
        }

        // A list-shaped signal: no top-level `value`, one entry per physical thing.
        void ListSignal(string name, IEnumerable<Dictionary<string, object?>> rows)
        {
            var stamp = Stamp(stamps, name);
            metrics[name] = rows.Select(row =>
            {
                row["updateTime"] = stamp;
                row["oemCorrelationId"] = (++correlation).ToString();
                return row;
            }).ToList();
        }

        Signal("acceleratorPedalPosition", driving ? 22 : 0);
        Signal("alarmStatus", "DISARMED", ("tags", new Dictionary<string, object?> { ["ALARM_SOURCE"] = "UNKNOWN" }));
        Signal("ambientTemp", 18.5);
        Signal("batteryLoadStatus", driving ? "DISCHARGING" : "NORMAL", ("vehicleBattery", "MAIN"));
        // The 12 V battery. Named almost identically to the traction battery and
        // completely unrelated to it — see the class comment.
        Signal("batteryStateOfCharge", 64, ("vehicleBattery", "MAIN"));
        Signal("batteryVoltage", 12.6, ("vehicleBattery", "MAIN"));
        Signal("brakePedalStatus", "NOT_APPLIED");
        Signal("brakeTorque", 0);
        Signal("compassDirection", Compass(car.HeadingDegrees), ("gpsModuleTimestamp", Stamp(stamps, "position")));
        // Null `value` with the real content in sibling keys. Another envelope a naive
        // renderer gets wrong.
        Signal("configurations", null,
            ("automaticSoftwareUpdateOptInSetting", "ON"),
            ("automaticSoftwareUpdateScheduleSetting", "IMMEDIATE"),
            ("remoteStartRunDurationSetting", 900),
            ("xevDepartureSchedulesSetting", "OFF"));
        Signal("displaySystemOfMeasure", "METRIC");
        Signal("engineCoolantTemp", 20);
        Signal("engineSpeed", 0);
        Signal("gearLeverPosition", driving ? "DRIVE" : "PARK");
        Signal("heading", new Dictionary<string, object?>
        {
            ["detectionType"] = "GPS",
            ["heading"] = Math.Round(car.HeadingDegrees, 1),
            ["uncertainty"] = 2.0,
        }, ("gpsModuleTimestamp", Stamp(stamps, "position")));
        Signal("hoodStatus", "CLOSED");
        Signal("hybridVehicleModeStatus", "EV_NOW");
        Signal(IgnitionSignal, car.Ignition);
        Signal(Odometer, (int)Math.Round(car.Odometer));
        Signal("oilLifeRemaining", 100);
        Signal("outsideTemperature", 18.0);
        Signal("panicAlarmStatus", "OFF");
        Signal("parkingBrakeStatus", driving ? "RELEASED" : "SET", ("parkingBrakeType", "ELECTRIC"));

        // Position, with the accuracy fields Ford ships around it.
        Signal("position", new Dictionary<string, object?>
        {
            ["location"] = new Dictionary<string, object?>
            {
                ["lat"] = Math.Round(car.Latitude, 6),
                ["lon"] = Math.Round(car.Longitude, 6),
            },
            ["gpsCoordinateMethod"] = "GPS",
            ["gpsDimension"] = "THREE_D",
            ["gdop"] = 1.4,
            ["hdop"] = 0.8,
            ["pdop"] = 1.1,
            ["vdop"] = 0.9,
        }, ("gpsModuleTimestamp", Stamp(stamps, "position")));

        Signal("remoteStartCountdownTimer", 0);
        Signal("speed", (int)Math.Round(car.Speed));
        Signal("torqueAtTransmission", driving ? 180 : 0);
        Signal("tripFuelEconomy", 0, ("tripProgress", "ONGOING"));
        Signal("tripXevBatteryChargeRegenerated", 0, ("tripProgress", "ONGOING"));
        Signal("tripXevBatteryRangeRegenerated", 0, ("tripProgress", "ONGOING"));
        Signal("vehicleLifeCycleMode", "NORMAL");
        Signal("wheelTorqueStatus", driving ? "APPLIED" : "NOT_APPLIED");
        Signal("xevBatteryActualStateOfCharge", Math.Round(car.StateOfCharge - 1.2, 1));
        Signal("xevBatteryCapacity", car.Seed.CapacityKwh);
        Signal("xevBatteryChargeDisplayStatus", car.ChargeStatus);
        Signal("xevBatteryChargerCurrentOutput", charging ? 16 : 0);
        Signal("xevBatteryChargerEnergyOutput", charging ? 11_000 : 0);
        Signal("xevBatteryChargerVoltageOutput", charging ? 400 : 0);
        Signal("xevBatteryEnergyRemaining", Math.Round(car.Seed.CapacityKwh * car.StateOfCharge / 100.0, 1));
        Signal("xevBatteryIoCurrent", charging ? 27.5 : driving ? -85.0 : 0.0);
        Signal("xevBatteryMaximumRange", 440);
        Signal("xevBatteryPerformanceStatus", "NORMAL");
        Signal("xevBatteryRange", (int)Math.Round(440 * car.StateOfCharge / 100.0));
        Signal(StateOfChargeSignal, Math.Round(car.StateOfCharge, 1));
        Signal("xevBatteryTemperature", 21.5);
        Signal("xevBatteryTimeToFullCharge", charging
            ? (int)Math.Round((100 - car.StateOfCharge) / 100.0 * car.Seed.CapacityKwh / 11.0 * 60)
            : 0);
        Signal("xevBatteryVoltage", 398.4);
        Signal("xevChargeStationCommunicationStatus", car.Plug == "CONNECTED" ? "AVAILABLE" : "UNAVAILABLE");
        Signal("xevChargeStationPowerType", car.Plug == "CONNECTED" ? "AC" : "NONE");
        // CONNECTED says a cable is in. It does NOT say current is flowing — that is
        // `xevBatteryChargeDisplayStatus` — and conflating the two is how an app claims
        // a charge is running while nothing moves.
        Signal("xevPlugChargerStatus", car.Plug);
        Signal("xevTractionMotorCurrent", driving ? 95 : 0);
        Signal("xevTractionMotorVoltage", driving ? 398 : 0);
        Signal("yawRate", 0.0);

        ListSignal("doorLockStatus", new[] { "ALL_DOORS", "DRIVER" }
            .Select(door => new Dictionary<string, object?> { ["value"] = "LOCKED", ["vehicleDoor"] = door }));

        ListSignal("doorStatus", new (string Door, string Side, string Role)[]
        {
            ("UNSPECIFIED_FRONT", "DRIVER", "DRIVER"),
            ("UNSPECIFIED_FRONT", "PASSENGER", "PASSENGER"),
            ("REAR_LEFT", "DRIVER", "NOT_APPLICABLE"),
            ("REAR_RIGHT", "PASSENGER", "NOT_APPLICABLE"),
            ("HOOD_DOOR", "UNSPECIFIED", "NOT_APPLICABLE"),
            ("TAILGATE", "UNSPECIFIED", "NOT_APPLICABLE"),
        }.Select(d => new Dictionary<string, object?>
        {
            ["value"] = "CLOSED",
            ["vehicleDoor"] = d.Door,
            ["vehicleSide"] = d.Side,
            ["vehicleOccupantRole"] = d.Role,
        }));

        ListSignal("seatBeltStatus", [new Dictionary<string, object?>
        {
            ["value"] = driving ? "BUCKLED" : "UNBUCKLED",
            ["vehicleOccupantRole"] = "DRIVER",
        }]);

        ListSignal("tirePressure", new[] { "FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT" }
            .Select(wheel => new Dictionary<string, object?>
            {
                ["value"] = 240,
                ["vehicleWheel"] = wheel,
                ["wheelPlacardFront"] = 240,
            }));

        ListSignal("tirePressureStatus", new[] { "FRONT_LEFT", "FRONT_RIGHT", "REAR_LEFT", "REAR_RIGHT" }
            .Select(wheel => new Dictionary<string, object?>
            {
                ["value"] = "NORMAL",
                ["vehicleWheel"] = wheel,
            }));

        ListSignal("tirePressureSystemStatus",
            [new Dictionary<string, object?> { ["value"] = "SYSTEM_OK" }]);

        ListSignal("windowStatus", new (string Window, string Side, string Role)[]
        {
            ("UNSPECIFIED_WINDOW", "DRIVER", "DRIVER"),
            ("UNSPECIFIED_WINDOW", "PASSENGER", "PASSENGER"),
            ("REAR_LEFT", "DRIVER", "NOT_APPLICABLE"),
            ("REAR_RIGHT", "PASSENGER", "NOT_APPLICABLE"),
        }.Select(w => new Dictionary<string, object?>
        {
            ["value"] = "FULLY_CLOSED",
            ["vehicleWindow"] = w.Window,
            ["vehicleSide"] = w.Side,
            ["vehicleOccupantRole"] = w.Role,
        }));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // The car owns this stamp, not the read. It moves when the car reports, sits
            // a little ahead of the metrics it carries, and occasionally moves alone —
            // see the measurement in this type's summary. Deduplicating on it is
            // therefore wrong, but only about one reading in twelve wrong, which is why
            // the emulator has to reproduce the rarity and not just the fact.
            ["updateTime"] = car.EnvelopeStamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["vehicleId"] = car.VehicleId,
            ["vin"] = car.Vin,
            ["metrics"] = metrics,
        };
    }

    private static string Stamp(IReadOnlyDictionary<string, DateTimeOffset> stamps, string name) =>
        (stamps.TryGetValue(name, out var at) ? at : DateTimeOffset.UtcNow)
            .ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string Compass(double heading) => heading switch
    {
        < 22.5 or >= 337.5 => "North",
        < 67.5 => "NorthEast",
        < 112.5 => "East",
        < 157.5 => "SouthEast",
        < 202.5 => "South",
        < 247.5 => "SouthWest",
        < 292.5 => "West",
        _ => "NorthWest",
    };
}
