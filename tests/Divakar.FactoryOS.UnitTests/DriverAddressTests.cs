using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Connectors.Drivers;
using Divakar.FactoryOS.Domain.Plant;

namespace Divakar.FactoryOS.UnitTests;

public sealed class DriverAddressTests
{
    [Theory]
    [InlineData("hr:40001:float32", ModbusArea.HoldingRegister, 0, ModbusType.Float32)]
    [InlineData("hr:10", ModbusArea.HoldingRegister, 10, ModbusType.UInt16)]
    [InlineData("ir:30010:int16", ModbusArea.InputRegister, 9, ModbusType.Int16)]
    [InlineData("coil:3", ModbusArea.Coil, 3, ModbusType.Bool)]
    [InlineData("di:10005", ModbusArea.DiscreteInput, 4, ModbusType.Bool)]
    public void Modbus_addresses_parse(string text, ModbusArea area, int offset, ModbusType type)
    {
        var a = ModbusAddress.Parse(text);
        Assert.Equal((area, offset, type), (a.Area, a.Offset, a.Type));
    }

    [Fact]
    public void Modbus_float_decodes_in_both_word_orders()
    {
        // 123.5f = 0x42F70000 -> registers [0x42F7, 0x0000] in Modbus (big-endian) order
        Assert.Equal(123.5, ModbusAddress.Parse("hr:0:float32").Decode([0x42F7, 0x0000]), 3);
        Assert.Equal(123.5, ModbusAddress.Parse("hr:0:float32:swap").Decode([0x0000, 0x42F7]), 3);
        Assert.Equal(-2, ModbusAddress.Parse("hr:0:int16").Decode([0xFFFE]));
        Assert.Equal(70000, ModbusAddress.Parse("hr:0:uint32").Decode([0x0001, 0x1170]));
    }

    [Fact]
    public void Modbus_rejects_bad_addresses()
    {
        Assert.Throws<FormatException>(() => ModbusAddress.Parse("xx:1"));
        Assert.Throws<FormatException>(() => ModbusAddress.Parse("hr:abc"));
        Assert.Throws<FormatException>(() => ModbusAddress.Parse("hr:1:decimal"));
    }

    [Fact]
    public void Modbus_groups_neighbouring_registers_into_one_request()
    {
        TagBinding T(string code) => new(Guid.NewGuid(), code, "temperature", "temp", null, 1, 0, 0, null, null);
        var items = new List<(TagBinding, ModbusAddress)>
        {
            (T("a"), ModbusAddress.Parse("hr:0:float32")), (T("b"), ModbusAddress.Parse("hr:2:float32")),
            (T("c"), ModbusAddress.Parse("hr:500")), (T("d"), ModbusAddress.Parse("ir:0")),
        };
        var blocks = ModbusConnector.Blocks(items);
        Assert.Equal(3, blocks.Count);   // hr 0-3 together, hr 500 alone (too far), ir alone
        Assert.Contains(blocks, b => b.Count == 2);
    }

    [Theory]
    [InlineData("plant/line1/temp", "plant/line1/temp", true)]
    [InlineData("plant/+/temp", "plant/line9/temp", true)]
    [InlineData("plant/#", "plant/a/b/c", true)]
    [InlineData("plant/+/temp", "plant/a/b/temp", false)]
    [InlineData("plant/line1", "plant/line1/temp", false)]
    public void Mqtt_topic_filters_match_like_a_broker(string filter, string topic, bool match) =>
        Assert.Equal(match, MqttAddress.Matches(filter, topic));

    [Fact]
    public void Mqtt_values_come_from_plain_or_json_payloads()
    {
        Assert.Equal(21.5, MqttAddress.Parse("t").Extract("21.5"));
        Assert.Equal(1, MqttAddress.Parse("t").Extract("ON"));
        var json = """{"power":{"kw":12.75},"phases":[{"v":229.1},{"v":230.4}],"ok":true,"s":"7"}""";
        Assert.Equal(12.75, MqttAddress.Parse("t|$.power.kw").Extract(json));
        Assert.Equal(230.4, MqttAddress.Parse("t|$.phases[1].v").Extract(json));
        Assert.Equal(1, MqttAddress.Parse("t|$.ok").Extract(json));
        Assert.Equal(7, MqttAddress.Parse("t|$.s").Extract(json));
        Assert.Null(MqttAddress.Parse("t|$.missing").Extract(json));
        Assert.Null(MqttAddress.Parse("t|$.power").Extract("not json"));
    }

    [Fact]
    public void S7_addresses_and_conversions()
    {
        Assert.Equal(("DB1.DBD0", "real"), (S7Address.Parse("db1.dbd0:real").Address, S7Address.Parse("db1.dbd0:real").Type));
        Assert.Equal("bool", S7Address.Parse("DB2.DBX0.1").Type);
        Assert.Equal("int", S7Address.Parse("MW10").Type);
        Assert.Throws<FormatException>(() => S7Address.Parse("DB1.DBQ0"));
        Assert.Equal(123.5, S7Connector.Convert(BitConverter.SingleToUInt32Bits(123.5f), "real")!.Value, 3);
        Assert.Equal(-5, S7Connector.Convert((ushort)0xFFFB, "int"));
        Assert.Equal(1, S7Connector.Convert(true, "bool"));
    }

    [Fact]
    public void MtConnect_current_document_is_read_into_numbers_and_states()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:1.3"><Streams><DeviceStream name="VMC">
              <ComponentStream><Samples><SpindleSpeed dataItemId="S1speed" timestamp="2026-10-16T00:00:00Z">8200.5</SpindleSpeed></Samples>
              <Events><Execution dataItemId="exec" timestamp="2026-10-16T00:00:00Z">ACTIVE</Execution>
                      <PartCount dataItemId="parts" timestamp="2026-10-16T00:00:00Z">1234</PartCount>
                      <Availability dataItemId="avail" timestamp="2026-10-16T00:00:00Z">UNAVAILABLE</Availability></Events>
              </ComponentStream></DeviceStream></Streams></MTConnectStreams>
            """;
        var v = MtConnectConnector.Parse(xml);
        Assert.Equal(8200.5, v["S1speed"]);
        Assert.Equal(MachineStates.Running, v["exec"]);
        Assert.Equal(1234, v["parts"]);
        Assert.Null(v["avail"]);
    }

    [Fact]
    public void Endpoints_split_into_host_and_port()
    {
        Assert.Equal(("10.0.0.5", 502), Endpoints.HostPort("10.0.0.5", 502));
        Assert.Equal(("plc1", 1502), Endpoints.HostPort("tcp://plc1:1502/", 502));
        Assert.Equal(("broker.local", 8883), Endpoints.HostPort("mqtts://broker.local:8883", 1883));
        Assert.Throws<FormatException>(() => Endpoints.HostPort("", 502));
    }

    [Fact]
    public void Secrets_are_masked_and_restored()
    {
        var stored = """{"username":"plc","password":"s3cret","unitId":1}""";
        var masked = SecretMask.Mask(stored)!;
        Assert.DoesNotContain("s3cret", masked);
        Assert.Contains(SecretMask.Masked, masked);
        var restored = SecretMask.Restore(masked.Replace("\"unitId\":1", "\"unitId\":2"), stored)!;
        Assert.Contains("s3cret", restored);
        Assert.Contains("\"unitId\":2", restored);
        Assert.Contains("n3w", SecretMask.Restore("""{"password":"n3w"}""", stored));
        Assert.Null(SecretMask.Restore("[1,2]", stored));
    }
}
