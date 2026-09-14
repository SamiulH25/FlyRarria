using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using FlyRarria.Brain;
using FlyRarria.Content.Pets;

namespace FlyRarria.Content.Debug
{
	/// <summary>
	/// Neuroscope: a screen overlay of the mote's whole connectome while it acts.
	/// The fly's brain (frontal view, seen from behind) and VNC are drawn from the real
	/// neuropil meshes, and every neuron is a dot where it sits in the fly (ScopeLayout).
	/// Neurons that spiked in the latest brain step glow, and their strongest outgoing
	/// synapses flash as lines, orange excitatory and blue inhibitory. Labels show the
	/// live sensory drives and readout rates. Everything drawn comes from
	/// MoteProjectile.LastSpikes, so nothing lights up without real spikes.
	/// </summary>
	public class Neuroscope : ModSystem
	{
		public static bool Visible;
		public static string KeyName => _toggle?.GetAssignedKeys().FirstOrDefault() ?? "unbound";
		private static ModKeybind _toggle;

		private const int Margin = 16, LeftGutter = 78, RightGutter = 92, Header = 26, Footer = 8;
		private const float LabelScale = 0.7f;
		private const int MaxLines = 2600;
		private const float GlowDecay = 0.8f; // per game tick; a brain step lands every 3
		private const float EdgeDecay = 0.78f;

		private static readonly Color ExcBase = new Color(80, 125, 145), InhBase = new Color(145, 85, 135);
		private static readonly Color ExcLine = new Color(255, 165, 60), InhLine = new Color(80, 150, 255);
		private static readonly Color ExcGlow = new Color(255, 235, 160), InhGlow = new Color(160, 200, 255);
		/// <summary>ScopeShape regions in draw order: cortex, neuropil fills, then outlines.</summary>
		private static readonly (string region, Color color)[] ShapeColors = {
			("rind", new Color(24, 26, 46)), ("central", new Color(34, 40, 70)), ("optic", new Color(28, 50, 60)),
			("vnc", new Color(44, 34, 62)), ("neuropil", new Color(62, 70, 106)), ("surface", new Color(110, 120, 165)),
		};

		private Connectome _graph;
		private ScopeLayout _layout;
		private string _layoutNote;
		private int _graphW = 300, _graphH = 325; // the ScopeShape's size once attached
		private float[] _glow;
		private float[] _edgeLife; // per ScopeLayout.TopEdges slot
		private readonly List<int> _activeEdges = new List<int>();
		private readonly Dictionary<(string, string), (float x, float y)?> _centroids = new Dictionary<(string, string), (float x, float y)?>();
		private int _lastBrainTick;
		private int _spikeCount;
		private readonly List<Label> _labels = new List<Label>();

		private int PanelW => LeftGutter + _graphW + RightGutter;
		private int PanelH => Header + _graphH + Footer;

		private struct Label
		{
			public string Text;
			public double Hz;
			public Color Color;
			public float AnchorX, AnchorY, Y;
		}

		public override void Load()
		{
			_toggle = KeybindLoader.RegisterKeybind(Mod, "Neuroscope", "N");
		}

		public override void Unload()
		{
			_toggle = null;
			Visible = false;
		}

		public static void ProcessToggle()
		{
			if (_toggle?.JustPressed == true) {
				Visible = !Visible;
			}
		}

		public override void PostUpdateProjectiles()
		{
			if (Main.dedServ || !Visible) {
				return;
			}
			var m = MoteProjectile.FindFor(Main.LocalPlayer);
			if (m?.Graph == null) {
				return;
			}
			if (!ReferenceEquals(m.Graph, _graph)) {
				Attach(m);
			}
			if (_layout == null) {
				return;
			}

			for (int i = 0; i < _glow.Length; i++) {
				_glow[i] *= GlowDecay;
			}
			for (int a = _activeEdges.Count - 1; a >= 0; a--) {
				int k = _activeEdges[a];
				float life = _edgeLife[k] * EdgeDecay;
				if (life < 0.05f) {
					life = 0f;
					_activeEdges[a] = _activeEdges[^1];
					_activeEdges.RemoveAt(_activeEdges.Count - 1);
				}
				_edgeLife[k] = life;
			}

			if (m.BrainTicks == _lastBrainTick) {
				return;
			}
			_lastBrainTick = m.BrainTicks;
			int[] spikes = m.LastSpikes;
			_spikeCount = spikes.Length;
			int[] top = _layout.TopEdges;
			foreach (int i in spikes) {
				_glow[i] = 1f;
				int end = (i + 1) * ScopeLayout.EdgesPerNeuron;
				for (int k = i * ScopeLayout.EdgesPerNeuron; k < end && top[k] >= 0; k++) {
					if (_edgeLife[k] <= 0f) {
						_activeEdges.Add(k);
					}
					_edgeLife[k] = 1f;
				}
			}
		}

		private void Attach(MoteProjectile m)
		{
			_graph = m.Graph;
			_layout = ScopeLayout.TryCreate(m.Graph, m.Pops, out _layoutNote);
			if (_layout != null) {
				_graphW = _layout.Shape.Width;
				_graphH = _layout.Shape.Height;
			}
			_glow = new float[_graph.NeuronCount];
			_edgeLife = new float[_graph.NeuronCount * ScopeLayout.EdgesPerNeuron];
			_activeEdges.Clear();
			_centroids.Clear();
			_lastBrainTick = m.BrainTicks;
			_spikeCount = 0;
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Mouse Text");
			var layer = new LegacyGameInterfaceLayer("FlyRarria: Neuroscope", Draw, InterfaceScaleType.UI);
			if (idx >= 0) {
				layers.Insert(idx, layer);
			}
			else {
				layers.Add(layer);
			}
		}

		private bool Draw()
		{
			if (!Visible) {
				return true;
			}
			var sb = Main.spriteBatch;
			var panel = new Rectangle(
				(int)(Main.screenWidth / Main.UIScale) - PanelW - Margin,
				(int)(Main.screenHeight / Main.UIScale) - PanelH - Margin,
				PanelW, PanelH);
			if (panel.Contains(Main.MouseScreen.ToPoint())) {
				Main.LocalPlayer.mouseInterface = true;
			}
			Utils.DrawInvBG(sb, panel, new Color(12, 14, 30) * 0.9f);

			var m = MoteProjectile.FindFor(Main.LocalPlayer);
			float hx = panel.X + 10, hy = panel.Y + 6;
			Utils.DrawBorderString(sb, "Neuroscope", new Vector2(hx, hy), Color.White, 0.8f);
			if (m == null || m.Graph == null || !ReferenceEquals(m.Graph, _graph) || _layout == null) {
				string why = m == null ? "summon the mote to see its brain"
					: m.Graph == null ? $"[reflex]: {CircuitLoader.LoadNote}"
					: !ReferenceEquals(m.Graph, _graph) ? "loading circuits..." : _layoutNote;
				Utils.DrawBorderString(sb, why, new Vector2(hx, hy + 30), new Color(170, 170, 180), LabelScale);
				return true;
			}

			Utils.DrawBorderString(sb, m.CurrentMode.ToString(), new Vector2(hx + 96, hy), MoteHud.ModeColor(m.CurrentMode), 0.8f);
			string stats = $"{_spikeCount} spikes / 50ms   {_graph.NeuronCount} neurons";
			float statsW = FontAssets.MouseText.Value.MeasureString(stats).X * LabelScale;
			Utils.DrawBorderString(sb, stats, new Vector2(panel.Right - 10 - statsW, hy + 2), new Color(170, 170, 180), LabelScale);

			var origin = new Vector2(panel.X + LeftGutter, panel.Y + Header);
			Vector2 Pos(float x, float y) => origin + new Vector2(x * _graphW, y * _graphH);

			// The brain itself: cortex, neuropils and outlines, one pixel row span at a time.
			var shape = _layout.Shape;
			foreach (var (region, color) in ShapeColors) {
				if (!shape.Spans.TryGetValue(region, out int[] s)) {
					continue;
				}
				for (int k = 0; k < s.Length; k += 3) {
					Rect(sb, origin + new Vector2(s[k + 1], s[k]), new Vector2(s[k + 2], 1), color);
				}
			}
			Utils.DrawBorderString(sb, "L", origin + new Vector2(2, 0), Color.White * 0.35f, 0.6f);
			Utils.DrawBorderString(sb, "R", origin + new Vector2(_graphW - 10, 0), Color.White * 0.35f, 0.6f);
			Utils.DrawBorderString(sb, "VNC", origin + new Vector2(2, shape.VncTop + 4), Color.White * 0.3f, 0.55f);

			// Resting wiring: every neuron, dim.
			for (int i = 0; i < _graph.NeuronCount; i++) {
				float size = _layout.IsReadout[i] ? 4f : 2f;
				Color c = (_graph.Signs[i] < 0 ? InhBase : ExcBase) * (_layout.IsReadout[i] ? 0.9f : 0.5f);
				Rect(sb, Pos(_layout.X[i], _layout.Y[i]) - new Vector2(size / 2f), new Vector2(size), c);
			}

			// Synapses carrying the latest spikes. Alpha 0 colours blend additively,
			// so busy pathways build up brighter than single lines.
			int stride = Math.Max(1, (_activeEdges.Count + MaxLines - 1) / MaxLines);
			for (int a = 0; a < _activeEdges.Count; a += stride) {
				int k = _activeEdges[a];
				int pre = k / ScopeLayout.EdgesPerNeuron;
				int post = _graph.Targets[_layout.TopEdges[k]];
				Color c = (_graph.Signs[pre] < 0 ? InhLine : ExcLine) * (_edgeLife[k] * 0.4f);
				c.A = 0;
				Line(sb, Pos(_layout.X[pre], _layout.Y[pre]), Pos(_layout.X[post], _layout.Y[post]), c);
			}

			// Spiking neurons.
			for (int i = 0; i < _graph.NeuronCount; i++) {
				float g = _glow[i];
				if (g < 0.06f) {
					continue;
				}
				float size = (_layout.IsReadout[i] ? 4f : 2f) + 2f * g;
				Color c = (_graph.Signs[i] < 0 ? InhGlow : ExcGlow) * g;
				c.A = 0;
				Rect(sb, Pos(_layout.X[i], _layout.Y[i]) - new Vector2(size / 2f), new Vector2(size), c);
			}

			DrawInputLabels(sb, m, origin, panel);
			DrawReadoutLabels(sb, m, origin, panel);
			return true;
		}

		private void DrawInputLabels(SpriteBatch sb, MoteProjectile m, Vector2 origin, Rectangle panel)
		{
			// Only channels being driven right now: this is what the fly is sensing.
			_labels.Clear();
			var seen = new Dictionary<string, int>();
			foreach (var (type, side, hz) in m.LastDrives) {
				string name = type.Replace("prefix:", "") + (side != null ? " " + side : "");
				if (seen.TryGetValue(name, out int at)) {
					if (hz > _labels[at].Hz) {
						_labels[at] = _labels[at] with { Text = $"{name} {hz:0}", Hz = hz };
					}
					continue;
				}
				if (Centroid(m, type, side) is not (float x, float y)) {
					continue;
				}
				seen[name] = _labels.Count;
				_labels.Add(new Label {
					Text = $"{name} {hz:0}", Hz = hz, Color = new Color(150, 235, 255),
					AnchorX = origin.X + x * _graphW, AnchorY = origin.Y + y * _graphH,
				});
			}
			PlaceLabels(sb, origin, rightSide: false, panel);
		}

		private void DrawReadoutLabels(SpriteBatch sb, MoteProjectile m, Vector2 origin, Rectangle panel)
		{
			_labels.Clear();
			foreach (var (type, side) in MotorDecoder.Readouts) {
				if (Centroid(m, type, side) is not (float x, float y)) {
					continue;
				}
				double hz = m.Net.MeanRateHz(m.Pops.Resolve(type, side));
				Color c = hz < 1 ? new Color(115, 115, 130)
					: Color.Lerp(new Color(200, 180, 120), new Color(255, 240, 150), (float)Math.Min(1, hz / 40));
				_labels.Add(new Label {
					Text = $"{type}{(side != null ? " " + side : "")} {hz:0}", Color = c,
					AnchorX = origin.X + x * _graphW, AnchorY = origin.Y + y * _graphH,
				});
			}
			PlaceLabels(sb, origin, rightSide: true, panel);
		}

		/// <summary>Stack labels in a gutter next to their anchors without overlap, with a faint leader line.</summary>
		private void PlaceLabels(SpriteBatch sb, Vector2 origin, bool rightSide, Rectangle panel)
		{
			float gap = 13f;
			float top = origin.Y, bottom = origin.Y + _graphH;
			_labels.Sort((p, q) => p.AnchorY.CompareTo(q.AnchorY));
			for (int i = 0; i < _labels.Count; i++) {
				float min = i == 0 ? top : _labels[i - 1].Y + gap;
				_labels[i] = _labels[i] with { Y = Math.Max(_labels[i].AnchorY - gap / 2f, min) };
			}
			float overflow = _labels.Count == 0 ? 0 : _labels[^1].Y + gap - bottom;
			for (int i = _labels.Count - 1; i >= 0 && overflow > 0; i--) {
				float max = i == _labels.Count - 1 ? bottom - gap : _labels[i + 1].Y - gap;
				_labels[i] = _labels[i] with { Y = Math.Max(top, Math.Min(_labels[i].Y, max)) };
			}

			var font = FontAssets.MouseText.Value;
			foreach (var label in _labels) {
				float w = font.MeasureString(label.Text).X * LabelScale;
				float x = rightSide ? origin.X + _graphW + 8 : panel.X + LeftGutter - 8 - w;
				float lineX = rightSide ? x - 2 : x + w + 2;
				Line(sb, new Vector2(lineX, label.Y + gap / 2f), new Vector2(label.AnchorX, label.AnchorY), label.Color * 0.25f);
				Utils.DrawBorderString(sb, label.Text, new Vector2(x, label.Y - 2), label.Color, LabelScale);
			}
		}

		private (float x, float y)? Centroid(MoteProjectile m, string type, string side)
		{
			if (!_centroids.TryGetValue((type, side), out var c)) {
				c = _layout.Centroid(m.Pops.Resolve(type, side));
				_centroids[(type, side)] = c;
			}
			return c;
		}

		private static void Rect(SpriteBatch sb, Vector2 pos, Vector2 size, Color color)
		{
			sb.Draw(TextureAssets.MagicPixel.Value, pos, new Rectangle(0, 0, 1, 1), color,
				0f, Vector2.Zero, size, SpriteEffects.None, 0f);
		}

		private static void Line(SpriteBatch sb, Vector2 a, Vector2 b, Color color)
		{
			Vector2 d = b - a;
			float len = d.Length();
			if (len < 1f) {
				return;
			}
			sb.Draw(TextureAssets.MagicPixel.Value, a, new Rectangle(0, 0, 1, 1), color,
				(float)Math.Atan2(d.Y, d.X), Vector2.Zero, new Vector2(len, 1f), SpriteEffects.None, 0f);
		}
	}

	public class NeuroscopePlayer : ModPlayer
	{
		public override void ProcessTriggers(TriggersSet triggersSet) => Neuroscope.ProcessToggle();
	}
}
