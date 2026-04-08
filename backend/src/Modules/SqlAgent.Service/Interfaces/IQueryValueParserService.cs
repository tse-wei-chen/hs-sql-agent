using System.Text.Json;

namespace SqlAgent.Service.Interfaces;

public interface IQueryValueParserService
{
	bool TryGetBetweenValues(object? rawValue, out object? start, out object? end);
	object UnwrapJsonElement(JsonElement je);
	bool TryToDateTime(object? value, out DateTime dateTime);
	bool TryGetInValues(object? value, out IEnumerable<object> values);
}
