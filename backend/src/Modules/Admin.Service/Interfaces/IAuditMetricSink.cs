using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IAuditMetricSink
{
    void Record(string action, string result, AuditEventContext eventContext);
}
