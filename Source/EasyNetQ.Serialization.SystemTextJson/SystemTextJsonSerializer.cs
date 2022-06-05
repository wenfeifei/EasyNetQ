using System;
using System.Buffers;
using EasyNetQ.Internals;

namespace EasyNetQ.Serialization.SystemTextJson;

public class SystemTextJsonSerializer : ISerializer
{
    public IMemoryOwner<byte> MessageToBytes(Type messageType, object message)
    {
        var stream = new ArrayPooledMemoryStream();
        System.Text.Json.JsonSerializer.Serialize(stream, message, messageType);
        return stream;
    }

    public object BytesToMessage(Type messageType, in ReadOnlyMemory<byte> bytes)
    {
        return System.Text.Json.JsonSerializer.Deserialize(bytes.Span, messageType);
    }
}
