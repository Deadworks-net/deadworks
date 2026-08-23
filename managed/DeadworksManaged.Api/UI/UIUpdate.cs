namespace DeadworksManaged.Api.UI;

/// <summary>
/// Fluent builder for batching multiple field updates and ops into a single
/// logical send call. Use via <see cref="UIPanel.Build"/>.
/// </summary>
public sealed class UIUpdate {
	private readonly UIPanel _panel;
	private readonly List<Step> _steps = new();

	internal UIUpdate(UIPanel panel) { _panel = panel; }

	private enum StepKind { Set, SetUnreliable, Clear, Raw, BuildLayout, DestroyLayout, SetStyle }
	private readonly record struct Step(StepKind Kind, string? Key, string? Value, string? Node = null);

	public UIUpdate Set(string key, object value) {
		_steps.Add(new Step(StepKind.Set, key, value?.ToString() ?? ""));
		return this;
	}

	public UIUpdate SetUnreliable(string key, object value) {
		_steps.Add(new Step(StepKind.SetUnreliable, key, value?.ToString() ?? ""));
		return this;
	}

	/// <summary>See <see cref="UIPanel.SetStyle"/>.</summary>
	public UIUpdate SetStyle(string nodeId, string property, string value) {
		_steps.Add(new Step(StepKind.SetStyle, property, value, nodeId));
		return this;
	}

	public UIUpdate Clear() {
		_steps.Add(new Step(StepKind.Clear, null, null));
		return this;
	}

	public UIUpdate Raw(string text) {
		_steps.Add(new Step(StepKind.Raw, null, text));
		return this;
	}

	public UIUpdate BuildLayout(UINode root) {
		var encoded = UITreeEncoder.Encode(root);
		var tokenized = UIStyleCompressor.Compress(encoded);
		var compressed = UILz77.Compress(tokenized);
		_steps.Add(new Step(StepKind.BuildLayout, null, compressed));
		return this;
	}

	public UIUpdate DestroyLayout() {
		_steps.Add(new Step(StepKind.DestroyLayout, null, null));
		return this;
	}

	public void SendTo(RecipientFilter to) {
		foreach (var step in _steps) {
			switch (step.Kind) {
				case StepKind.Set:
					UIChannel.EnqueueSet(to, _panel.Id, step.Key!, step.Value!, unreliable: false);
					break;
				case StepKind.SetUnreliable:
					UIChannel.EnqueueSet(to, _panel.Id, step.Key!, step.Value!, unreliable: true);
					break;
				case StepKind.SetStyle:
					UIChannel.EnqueueStyle(to, _panel.Id, step.Node!, new[] { (step.Key!, step.Value!) });
					break;
				case StepKind.Clear:
					UIChannel.EnqueueClear(to, _panel.Id);
					break;
				case StepKind.Raw:
					UIChannel.EnqueueRaw(to, _panel.Id, step.Value!);
					break;
				case StepKind.BuildLayout:
					UIChannel.EnqueueBuild(to, _panel.Id, step.Value!);
					break;
				case StepKind.DestroyLayout:
					UIChannel.EnqueueDestroy(to, _panel.Id);
					break;
			}
		}
	}
}
