using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace Sts2Bridge.Bridge;

internal static partial class BridgeRuntime
{
    private const string BridgeNodeName = "Sts2Bridge";
    private static BridgeNode? _bridgeNode;

    public static void Initialize()
    {
        if (_bridgeNode is not null)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Warn("STS2 Bridge did not start: SceneTree unavailable.");
            return;
        }

        TraceRecorder.Start();
        ExportHooks.Apply();
        ActiveScreenContext.Instance.Updated += RequestExport;

        if (tree.Root.GetNodeOrNull<BridgeNode>(BridgeNodeName) is BridgeNode existingNode)
        {
            _bridgeNode = existingNode;
            return;
        }

        BridgeNode bridgeNode = new();
        bridgeNode.Name = BridgeNodeName;
        tree.Root.CallDeferred(Node.MethodName.AddChild, bridgeNode);
        _bridgeNode = bridgeNode;

        Log.Warn($"STS2 Bridge writing state to {StateExporter.StateFilePath}");
    }

    public static void RequestExport()
    {
        if (_bridgeNode is null)
        {
            Initialize();
        }

        _bridgeNode?.RequestExport();
    }

    private sealed partial class BridgeNode : Node
    {
        private const double FallbackPollSeconds = 3.0;
        private const double CommandPollSeconds = 0.1;
        private const double DebounceSeconds = 0.15;
        private string? _lastStableStateJson;
        private double _elapsedSeconds;
        private double _commandElapsedSeconds;
        private double _pendingElapsedSeconds;
        private bool _exportRequested;
        private int _errorCount;

        public override void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            SetProcess(true);
            RequestExport();
        }

        public override void _Process(double delta)
        {
            _elapsedSeconds += delta;
            _commandElapsedSeconds += delta;

            if (_commandElapsedSeconds >= CommandPollSeconds)
            {
                _commandElapsedSeconds = 0;
                CommandProcessor.ProcessPendingCommand();
            }

            if (_exportRequested)
            {
                _pendingElapsedSeconds += delta;
                if (_pendingElapsedSeconds >= DebounceSeconds)
                {
                    ExportIfChanged();
                }
            }

            if (_elapsedSeconds >= FallbackPollSeconds)
            {
                ExportIfChanged();
            }
        }

        public void RequestExport()
        {
            _exportRequested = true;
            _pendingElapsedSeconds = 0;
        }

        private void ExportIfChanged()
        {
            _exportRequested = false;
            _pendingElapsedSeconds = 0;
            _elapsedSeconds = 0;

            try
            {
                StableBridgeSnapshot stableSnapshot = StateExporter.BuildStableSnapshot();
                string stableStateJson = StateExporter.BuildStableStateJson(stableSnapshot);
                if (stableStateJson == _lastStableStateJson)
                {
                    return;
                }

                string stateJson = StateExporter.BuildStateJson(stableSnapshot);
                StateExporter.WriteStateJson(stateJson, stableSnapshot);
                TraceRecorder.RecordState(stateJson);
                _lastStableStateJson = stableStateJson;
                _errorCount = 0;
            }
            catch (Exception exception)
            {
                if (_errorCount < 5)
                {
                    Log.Error($"STS2 Bridge export failed: {exception}");
                    TraceRecorder.Record("export_error", new
                    {
                        error = exception.ToString(),
                        screen = ScreenDiagnostics.CaptureForError(),
                    });
                }

                _errorCount += 1;
            }
        }

        public override void _ExitTree()
        {
            if (ReferenceEquals(_bridgeNode, this))
            {
                ActiveScreenContext.Instance.Updated -= BridgeRuntime.RequestExport;
                TraceRecorder.Record("session_end", new { reason = "bridge_node_exited" });
                _bridgeNode = null;
            }
        }
    }
}
