"""See inside a run: the framework's OpenTelemetry spans, printed as they finish.

DEMONSTRATION CODE - written to be read rather than deployed.

Microsoft Agent Framework is instrumented with OpenTelemetry. Every workflow run, executor step,
agent run, model call and tool call becomes a span, with timings and token counts, and the
framework emits them only when you turn instrumentation on. This file turns it on and attaches two
consumers:

  - a printer, so `python m3_run.py start --telemetry` shows the spans in the terminal;
  - Azure Monitor, if APPLICATIONINSIGHTS_CONNECTION_STRING is set, so the same spans reach
    Application Insights, where a team would watch them in production.

Neither changes what the desk does. Prompts and replies are NOT recorded: the framework keeps
message content out of spans unless you opt in to sensitive data, and this desk does not.
"""
import os
import sys

from opentelemetry import trace
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import ReadableSpan, Span, SpanProcessor, TracerProvider

from agent_framework.observability import enable_instrumentation

# The span names already say what ran (an executor, an agent, a model call, a tool); these are the
# attributes worth a glance beside them: what each model call cost, in tokens.
SHOWN = {"in": "gen_ai.usage.input_tokens", "out": "gen_ai.usage.output_tokens"}


class PrintSpans(SpanProcessor):
    """One line per finished span, indented by nesting, on stderr so it never mixes with results."""

    def __init__(self) -> None:
        self._depth: dict[int, int] = {}

    def on_start(self, span: Span, parent_context=None) -> None:
        parent = span.parent
        self._depth[span.context.span_id] = (self._depth.get(parent.span_id, -1) + 1) if parent else 0

    def on_end(self, span: ReadableSpan) -> None:
        depth = self._depth.pop(span.context.span_id, 0)
        ms = (span.end_time - span.start_time) / 1e6 if span.end_time and span.start_time else 0
        attrs = span.attributes or {}
        extra = "  ".join(f"{label}={attrs[key]:,}" for label, key in SHOWN.items() if key in attrs)
        print(f"  [span] {'  ' * depth}{span.name}  {ms:,.0f} ms  {extra}".rstrip(),
              file=sys.stderr)


def enable() -> None:
    """Turn on the framework's instrumentation and send its spans to the printer, and to
    Application Insights when a connection string is set."""
    provider = TracerProvider(resource=Resource.create({"service.name": "claims365-fraud-desk"}))
    provider.add_span_processor(PrintSpans())
    conn = os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING")
    if conn:
        from azure.monitor.opentelemetry.exporter import AzureMonitorTraceExporter
        from opentelemetry.sdk.trace.export import BatchSpanProcessor
        provider.add_span_processor(
            BatchSpanProcessor(AzureMonitorTraceExporter.from_connection_string(conn)))
    trace.set_tracer_provider(provider)
    enable_instrumentation(enable_sensitive_data=False)
