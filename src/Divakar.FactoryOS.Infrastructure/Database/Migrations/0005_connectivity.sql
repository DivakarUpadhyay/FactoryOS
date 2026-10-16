-- Divakar FactoryOS 0005: connectivity (sensor catalogue, tag templates, tags, device keys) and telemetry storage

-- How each protocol is connected today: Native = built-in driver, Gateway = through an OPC UA / MQTT gateway,
-- Push = devices send to the API, Planned = driver arrives in a later phase.
ALTER TABLE plant.Protocols ADD SupportLevel varchar(10) NOT NULL CONSTRAINT DF_Protocols_Support DEFAULT 'Gateway'
    CONSTRAINT CK_Protocols_Support CHECK (SupportLevel IN ('Native','Gateway','Push','Planned'));
GO
UPDATE plant.Protocols SET SupportLevel = 'Native' WHERE ProtocolCode IN ('simulator','opcua','mqtt','modbus-tcp','s7','mtconnect');
UPDATE plant.Protocols SET SupportLevel = 'Push'   WHERE ProtocolCode IN ('http','nbiot','lorawan');
UPDATE plant.Protocols SET SupportLevel = 'Planned' WHERE ProtocolCode IN ('sparkplugb','vda5050','ros2','ur-rtde','secs-gem');
UPDATE plant.Protocols SET Notes = N'Gateway: connect through an OPC UA or MQTT gateway (Kepware, Node-RED, Ignition); '
                                   + ISNULL(Notes, N'')
WHERE SupportLevel = 'Gateway';

-- Every kind of measurement the platform understands. SimProfile tells the simulator how the value behaves.
CREATE TABLE plant.SensorTypes (
    SensorTypeCode  varchar(40)    NOT NULL CONSTRAINT PK_SensorTypes PRIMARY KEY,
    Category        varchar(30)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    Unit            nvarchar(20)   NOT NULL,
    MinValue        float          NULL,
    MaxValue        float          NULL,
    SimProfile      varchar(30)    NOT NULL
);

INSERT plant.SensorTypes (SensorTypeCode, Category, Name, Unit, MinValue, MaxValue, SimProfile) VALUES
 ('machine_state',     'Status',      N'Machine state (PackML)',       N'',      0,    5,     'state'),
 ('part_count',        'Production',  N'Good part counter',            N'pcs',   0,    NULL,  'counter_parts'),
 ('scrap_count',       'Production',  N'Scrap counter',                N'pcs',   0,    NULL,  'counter_scrap'),
 ('cycle_time',        'Production',  N'Cycle time',                   N's',     0,    600,   'cycle'),
 ('spindle_load',      'Production',  N'Spindle load',                 N'%',     0,    100,   'load'),
 ('spindle_speed',     'Production',  N'Spindle speed',                N'rpm',   0,    24000, 'speed'),
 ('feed_rate',         'Production',  N'Feed rate',                    N'mm/min',0,    20000, 'speed'),
 ('temperature',       'Process',     N'Temperature',                  N'°C',    -20,  250,   'temp'),
 ('oil_temperature',   'Process',     N'Oil temperature',              N'°C',    10,   120,   'temp'),
 ('humidity',          'Environment', N'Relative humidity',            N'%RH',   0,    100,   'env_humidity'),
 ('pressure',          'Process',     N'Pressure',                     N'bar',   0,    16,    'pressure'),
 ('vibration_rms',     'Condition',   N'Vibration (RMS velocity)',     N'mm/s',  0,    50,    'vibration'),
 ('vibration_peak',    'Condition',   N'Vibration (peak acceleration)',N'g',     0,    20,    'vibration'),
 ('bearing_temp',      'Condition',   N'Bearing temperature',          N'°C',    10,   130,   'temp'),
 ('motor_current',     'Electrical',  N'Motor current',                N'A',     0,    400,   'current'),
 ('voltage_ln',        'Electrical',  N'Voltage L-N',                  N'V',     180,  260,   'voltage'),
 ('current',           'Electrical',  N'Current',                      N'A',     0,    2000,  'current'),
 ('active_power',      'Electrical',  N'Active power',                 N'kW',    0,    NULL,  'power'),
 ('reactive_power',    'Electrical',  N'Reactive power',               N'kvar',  0,    NULL,  'reactive'),
 ('power_factor',      'Electrical',  N'Power factor',                 N'',      0,    1,     'pf'),
 ('frequency',         'Electrical',  N'Frequency',                    N'Hz',    59,   61,    'frequency'),
 ('thd_voltage',       'Electrical',  N'Voltage THD',                  N'%',     0,    20,    'thd'),
 ('thd_current',       'Electrical',  N'Current THD',                  N'%',     0,    60,    'thd'),
 ('energy_kwh',        'Electrical',  N'Active energy (import)',       N'kWh',   0,    NULL,  'counter_energy'),
 ('demand_kw',         'Electrical',  N'15-minute demand',             N'kW',    0,    NULL,  'power'),
 ('flow_water',        'Utility',     N'Water flow',                   N'm³/h',  0,    200,   'flow'),
 ('flow_gas',          'Utility',     N'Gas flow',                     N'Nm³/h', 0,    500,   'flow'),
 ('flow_air',          'Utility',     N'Compressed-air flow',          N'Nm³/min',0,   60,    'flow'),
 ('volume_water',      'Utility',     N'Water volume',                 N'm³',    0,    NULL,  'counter_volume'),
 ('volume_gas',        'Utility',     N'Gas volume',                   N'Nm³',   0,    NULL,  'counter_volume'),
 ('level',             'Process',     N'Tank level',                   N'%',     0,    100,   'level'),
 ('ph',                'Process',     N'pH',                           N'pH',    0,    14,    'ph'),
 ('conductivity',      'Process',     N'Conductivity',                 N'µS/cm', 0,    5000,  'env_slow'),
 ('weight',            'Process',     N'Weight (load cell)',           N'kg',    0,    5000,  'weight'),
 ('torque',            'Process',     N'Torque',                       N'N·m',   0,    1000,  'load'),
 ('gas_co',            'Safety',      N'Carbon monoxide',              N'ppm',   0,    500,   'gas'),
 ('gas_co2',           'Environment', N'CO₂',                          N'ppm',   350,  5000,  'co2'),
 ('smoke',             'Safety',      N'Smoke detected',               N'',      0,    1,     'bool_rare'),
 ('pm25',              'Environment', N'Particulate PM2.5',            N'µg/m³', 0,    500,   'env_slow'),
 ('noise',             'Environment', N'Noise level',                  N'dB(A)', 30,   120,   'noise'),
 ('light',             'Environment', N'Illuminance',                  N'lux',   0,    2000,  'env_slow'),
 ('thermal_max',       'Vision',      N'Hot-spot temperature',         N'°C',    0,    300,   'temp'),
 ('defect_count',      'Vision',      N'Defects detected',             N'pcs',   0,    NULL,  'counter_scrap'),
 ('door_open',         'Safety',      N'Door / guard open',            N'',      0,    1,     'bool_rare'),
 ('occupancy',         'Environment', N'People present',               N'',      0,    50,    'occupancy'),
 ('rfid_reads',        'Logistics',   N'RFID tag reads',               N'reads', 0,    NULL,  'counter_parts'),
 ('conveyor_speed',    'Logistics',   N'Conveyor speed',               N'm/min', 0,    120,   'speed'),
 ('battery_soc',       'Robotics',    N'Battery state of charge',      N'%',     0,    100,   'battery'),
 ('robot_joint_temp',  'Robotics',    N'Robot joint temperature',      N'°C',    15,   90,    'temp'),
 ('robot_joint_torque','Robotics',    N'Robot joint torque',           N'%',     0,    100,   'load'),
 ('robot_tcp_speed',   'Robotics',    N'Tool centre point speed',      N'mm/s',  0,    2000,  'speed'),
 ('position_x',        'Robotics',    N'Position X',                   N'm',     0,    200,   'position_x'),
 ('position_y',        'Robotics',    N'Position Y',                   N'm',     0,    100,   'position_y'),
 ('vehicle_speed',     'Robotics',    N'Vehicle speed',                N'm/s',   0,    3,     'speed'),
 ('irradiance',        'Energy',      N'Solar irradiance',             N'W/m²',  0,    1200,  'solar'),
 ('dc_power',          'Energy',      N'DC power',                     N'kW',    0,    NULL,  'solar_power'),
 ('alarm_code',        'Status',      N'Active alarm code',            N'',      0,    9999,  'alarm');

-- The tags an asset of each type starts with when "Apply template" is used.
CREATE TABLE plant.TagTemplates (
    AssetTypeCode   varchar(40)    NOT NULL CONSTRAINT FK_TagTemplates_Type REFERENCES plant.AssetTypes(AssetTypeCode),
    TagCode         varchar(40)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    SensorTypeCode  varchar(40)    NOT NULL CONSTRAINT FK_TagTemplates_Sensor REFERENCES plant.SensorTypes(SensorTypeCode),
    CONSTRAINT PK_TagTemplates PRIMARY KEY (AssetTypeCode, TagCode)
);

INSERT plant.TagTemplates (AssetTypeCode, TagCode, Name, SensorTypeCode) VALUES
 ('cnc','state',N'Machine state','machine_state'), ('cnc','parts',N'Good parts','part_count'), ('cnc','scrap',N'Scrap','scrap_count'),
 ('cnc','cycle',N'Cycle time','cycle_time'), ('cnc','spindle_load',N'Spindle load','spindle_load'), ('cnc','spindle_rpm',N'Spindle speed','spindle_speed'),
 ('cnc','vibration',N'Spindle vibration','vibration_rms'), ('cnc','power',N'Power','active_power'), ('cnc','energy',N'Energy','energy_kwh'),
 ('injection','state',N'Machine state','machine_state'), ('injection','parts',N'Good parts','part_count'), ('injection','scrap',N'Scrap','scrap_count'),
 ('injection','cycle',N'Cycle time','cycle_time'), ('injection','barrel_temp',N'Barrel temperature','temperature'), ('injection','oil_temp',N'Hydraulic oil temperature','oil_temperature'),
 ('injection','pressure',N'Injection pressure','pressure'), ('injection','power',N'Power','active_power'), ('injection','energy',N'Energy','energy_kwh'),
 ('press','state',N'Machine state','machine_state'), ('press','parts',N'Strokes','part_count'), ('press','tonnage',N'Tonnage','weight'),
 ('press','vibration',N'Frame vibration','vibration_peak'), ('press','power',N'Power','active_power'), ('press','energy',N'Energy','energy_kwh'),
 ('smt','state',N'Line state','machine_state'), ('smt','parts',N'Boards placed','part_count'), ('smt','defects',N'Placement defects','defect_count'),
 ('smt','cycle',N'Board cycle time','cycle_time'), ('smt','power',N'Power','active_power'), ('smt','energy',N'Energy','energy_kwh'),
 ('reflow','state',N'Oven state','machine_state'), ('reflow','zone_temp',N'Peak zone temperature','temperature'), ('reflow','conveyor',N'Conveyor speed','conveyor_speed'),
 ('reflow','power',N'Power','active_power'), ('reflow','energy',N'Energy','energy_kwh'),
 ('packaging','state',N'Machine state','machine_state'), ('packaging','parts',N'Packs','part_count'), ('packaging','scrap',N'Rejects','scrap_count'),
 ('packaging','power',N'Power','active_power'), ('packaging','energy',N'Energy','energy_kwh'),
 ('conveyor','state',N'Conveyor state','machine_state'), ('conveyor','speed',N'Speed','conveyor_speed'), ('conveyor','current',N'Motor current','motor_current'),
 ('conveyor','energy',N'Energy','energy_kwh'),
 ('robot-arm','state',N'Robot state','machine_state'), ('robot-arm','cycles',N'Cycles','part_count'), ('robot-arm','cycle',N'Cycle time','cycle_time'),
 ('robot-arm','j1_temp',N'Joint 1 temperature','robot_joint_temp'), ('robot-arm','j2_torque',N'Joint 2 torque','robot_joint_torque'),
 ('robot-arm','tcp_speed',N'TCP speed','robot_tcp_speed'), ('robot-arm','alarm',N'Alarm code','alarm_code'), ('robot-arm','power',N'Power','active_power'),
 ('robot-arm','energy',N'Energy','energy_kwh'),
 ('cobot','state',N'Cobot state','machine_state'), ('cobot','cycles',N'Cycles','part_count'), ('cobot','j1_temp',N'Joint 1 temperature','robot_joint_temp'),
 ('cobot','tcp_speed',N'TCP speed','robot_tcp_speed'), ('cobot','power',N'Power','active_power'),
 ('agv','state',N'Vehicle state','machine_state'), ('agv','battery',N'Battery','battery_soc'), ('agv','x',N'Position X','position_x'),
 ('agv','y',N'Position Y','position_y'), ('agv','speed',N'Speed','vehicle_speed'), ('agv','missions',N'Missions done','part_count'),
 ('amr','state',N'Robot state','machine_state'), ('amr','battery',N'Battery','battery_soc'), ('amr','x',N'Position X','position_x'),
 ('amr','y',N'Position Y','position_y'), ('amr','speed',N'Speed','vehicle_speed'), ('amr','missions',N'Missions done','part_count'),
 ('energy-meter','power',N'Active power','active_power'), ('energy-meter','energy',N'Energy','energy_kwh'), ('energy-meter','voltage',N'Voltage L1-N','voltage_ln'),
 ('energy-meter','current',N'Current L1','current'), ('energy-meter','pf',N'Power factor','power_factor'), ('energy-meter','demand',N'15-min demand','demand_kw'),
 ('power-analyzer','voltage',N'Voltage L1-N','voltage_ln'), ('power-analyzer','current',N'Current L1','current'), ('power-analyzer','power',N'Active power','active_power'),
 ('power-analyzer','reactive',N'Reactive power','reactive_power'), ('power-analyzer','pf',N'Power factor','power_factor'), ('power-analyzer','freq',N'Frequency','frequency'),
 ('power-analyzer','thd_v',N'Voltage THD','thd_voltage'), ('power-analyzer','thd_i',N'Current THD','thd_current'), ('power-analyzer','energy',N'Energy','energy_kwh'),
 ('transformer','oil_temp',N'Oil temperature','oil_temperature'), ('transformer','winding_temp',N'Winding temperature','temperature'), ('transformer','load',N'Load','active_power'),
 ('ups','battery',N'Battery','battery_soc'), ('ups','load',N'Output power','active_power'), ('ups','voltage',N'Output voltage','voltage_ln'),
 ('solar-inverter','irradiance',N'Irradiance','irradiance'), ('solar-inverter','dc_power',N'DC power','dc_power'), ('solar-inverter','ac_power',N'AC power','active_power'),
 ('solar-inverter','energy',N'Energy produced','energy_kwh'),
 ('battery-storage','soc',N'State of charge','battery_soc'), ('battery-storage','power',N'Charge/discharge power','active_power'), ('battery-storage','temp',N'Cell temperature','temperature'),
 ('ev-charger','power',N'Charging power','active_power'), ('ev-charger','energy',N'Energy delivered','energy_kwh'),
 ('air-compressor','state',N'Compressor state','machine_state'), ('air-compressor','pressure',N'Line pressure','pressure'), ('air-compressor','flow',N'Air flow','flow_air'),
 ('air-compressor','power',N'Power','active_power'), ('air-compressor','energy',N'Energy','energy_kwh'), ('air-compressor','oil_temp',N'Oil temperature','oil_temperature'),
 ('chiller','state',N'Chiller state','machine_state'), ('chiller','supply_temp',N'Chilled-water supply','temperature'), ('chiller','power',N'Power','active_power'),
 ('chiller','energy',N'Energy','energy_kwh'), ('chiller','flow',N'Water flow','flow_water'),
 ('boiler','state',N'Boiler state','machine_state'), ('boiler','steam_pressure',N'Steam pressure','pressure'), ('boiler','temp',N'Flue temperature','temperature'),
 ('boiler','gas_flow',N'Gas flow','flow_gas'), ('boiler','gas_volume',N'Gas used','volume_gas'),
 ('hvac','supply_temp',N'Supply air temperature','temperature'), ('hvac','humidity',N'Humidity','humidity'), ('hvac','co2',N'CO₂','gas_co2'),
 ('hvac','power',N'Fan power','active_power'), ('hvac','energy',N'Energy','energy_kwh'),
 ('water-meter','flow',N'Flow','flow_water'), ('water-meter','volume',N'Volume','volume_water'),
 ('gas-meter','flow',N'Flow','flow_gas'), ('gas-meter','volume',N'Volume','volume_gas'),
 ('plc','state',N'PLC run state','machine_state'), ('plc','alarm',N'Alarm code','alarm_code'),
 ('gateway','state',N'Gateway state','machine_state'),
 ('sensor','temp',N'Temperature','temperature'), ('sensor','humidity',N'Humidity','humidity'), ('sensor','pm25',N'PM2.5','pm25'),
 ('sensor','noise',N'Noise','noise'), ('sensor','light',N'Illuminance','light'), ('sensor','occupancy',N'Occupancy','occupancy'),
 ('camera','defects',N'Defects detected','defect_count'), ('camera','parts',N'Parts inspected','part_count'),
 ('thermal-camera','hotspot',N'Hot-spot temperature','thermal_max'),
 ('rfid-reader','reads',N'Tag reads','rfid_reads'),
 ('scanner','reads',N'Scans','rfid_reads'),
 ('gas-detector','co',N'Carbon monoxide','gas_co'), ('gas-detector','smoke',N'Smoke','smoke'),
 ('safety-plc','guard',N'Guard door open','door_open'), ('safety-plc','alarm',N'Safety alarm code','alarm_code');

-- Data points of one asset
CREATE TABLE plant.Tags (
    TagId           uniqueidentifier NOT NULL CONSTRAINT PK_Tags PRIMARY KEY CONSTRAINT DF_Tags_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Tags_Tenant REFERENCES core.Tenants(TenantId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_Tags_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    SensorTypeCode  varchar(40)    NOT NULL CONSTRAINT FK_Tags_Sensor REFERENCES plant.SensorTypes(SensorTypeCode),
    Unit            nvarchar(20)   NOT NULL,
    Address         nvarchar(300)  NULL,       -- ns=2;s=Temp | hr:40001:float32 | DB1.DBD0:real | topic|$.path | MTConnect dataItemId
    Scale           float          NOT NULL CONSTRAINT DF_Tags_Scale DEFAULT 1,
    Offset          float          NOT NULL CONSTRAINT DF_Tags_Offset DEFAULT 0,
    Deadband        float          NOT NULL CONSTRAINT DF_Tags_Deadband DEFAULT 0,   -- store only changes bigger than this
    MinValue        float          NULL,
    MaxValue        float          NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_Tags_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Tags_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Tags_Code UNIQUE (AssetId, Code)
);
CREATE INDEX IX_Tags_Tenant ON plant.Tags(TenantId, AssetId);

-- Connection health per asset, kept by the ingestion worker
ALTER TABLE plant.Assets ADD
    PollMs            int            NOT NULL CONSTRAINT DF_Assets_PollMs DEFAULT 5000,
    ConnectorSettings nvarchar(max)  NULL CONSTRAINT CK_Assets_Settings CHECK (ConnectorSettings IS NULL OR ISJSON(ConnectorSettings) = 1),
    LastSeenAt        datetime2(3)   NULL,
    LastError         nvarchar(500)  NULL;
GO

-- Keys that let a device post readings over HTTP (stored hashed; shown once when created)
CREATE TABLE plant.DeviceKeys (
    KeyHash         char(64)       NOT NULL CONSTRAINT PK_DeviceKeys PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_DeviceKeys_Tenant REFERENCES core.Tenants(TenantId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_DeviceKeys_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    Prefix          char(8)        NOT NULL,   -- first characters, to tell keys apart in the UI
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_DeviceKeys_CreatedAt DEFAULT SYSUTCDATETIME(),
    LastUsedAt      datetime2(0)   NULL,
    RevokedAt       datetime2(0)   NULL
);
CREATE INDEX IX_DeviceKeys_Asset ON plant.DeviceKeys(TenantId, AssetId);
GO

CREATE SCHEMA telemetry;
GO

-- Raw readings, stored by exception (deadband) plus a heartbeat. Columnstore keeps years of data small and fast to scan.
CREATE TABLE telemetry.Readings (
    TenantId        uniqueidentifier NOT NULL,
    TagId           uniqueidentifier NOT NULL,
    Ts              datetime2(3)   NOT NULL,
    Value           float          NULL,
    Quality         tinyint        NOT NULL    -- 0 good, 1 uncertain, 2 bad, 3 offline
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_Readings ON telemetry.Readings;
CREATE INDEX IX_Readings_Tag_Ts ON telemetry.Readings(TagId, Ts) INCLUDE (Value, Quality);

-- The newest value of every tag, for live screens
CREATE TABLE telemetry.Latest (
    TagId           uniqueidentifier NOT NULL CONSTRAINT PK_Latest PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL,
    Ts              datetime2(3)   NOT NULL,
    Value           float          NULL,
    Quality         tinyint        NOT NULL
);

CREATE TYPE telemetry.ReadingRows AS TABLE (
    TenantId        uniqueidentifier NOT NULL,
    TagId           uniqueidentifier NOT NULL,
    Ts              datetime2(3)   NOT NULL,
    Value           float          NULL,
    Quality         tinyint        NOT NULL,
    Store           bit            NOT NULL    -- 1 = also append to Readings
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Tags,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Tags,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.DeviceKeys,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.DeviceKeys,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON telemetry.Readings,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON telemetry.Latest;
GO
