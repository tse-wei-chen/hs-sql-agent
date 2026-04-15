using System.Text.Json;

namespace SqlAgent.Service.Interfaces;

public interface IQueryValueParserService
{
    object UnwrapJsonElement(JsonElement je);
    bool TryToDateTime(object? value, out DateTime dateTime);
    bool TryGetInValues(object? value, out IEnumerable<object> values);
    bool TryGetRangeValues(object? value, out object? start, out object? end);
}
