using NLog;
using NLog.Layouts;
using NLog.Targets;

namespace PhigrosSaveDumper;

public record class LogEvent(LogEventInfo Info, string Rendered);
public delegate void LogEventHandler(EventLogTarget sender, LogEvent e);
[Target("EventLog")]
public class EventLogTarget : Target
{
	public event LogEventHandler? LogEmitted;

	public List<LogEvent> AllLogs { get; } = [];
	public Layout Layout { get; set; } = "${message}";

	protected override void Write(LogEventInfo logEvent)
	{
		string rendered = this.RenderLogEvent(this.Layout, logEvent);
		LogEvent log = new(logEvent, rendered);
		this.AllLogs.Add(log);
		LogEmitted?.Invoke(this, log);
	}
}
