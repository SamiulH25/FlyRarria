namespace FlyRarria.Brain
{
	/// <summary>
	/// Reads descending/motor population rates into a movement command.
	/// Priority ladder (first match wins, with hysteresis + min dwell handled by
	/// the caller): ESCAPE > FEED > STARTLE > FOLLOW > GROOM > SONG > IDLE.
	/// Thresholds are hand-built scaffolding; the decisions (which populations
	/// fire) come from the wiring. Tune per circuit set in config, not in code.
	/// </summary>
	public enum MoteMode
	{
		Idle,
		Follow,
		Escape,
		Startle,
		Feed,
		Groom,
		Song,
		Sleep,
	}

	public struct MotorCommand
	{
		public MoteMode Mode;
		public float Forward;
		public float Yaw;
		public bool Reflex;
	}

	public sealed class MotorDecoder
	{
		public struct Thresholds
		{
			public double FeedMn9Hz;
			public double GroomADnHz;
			public double SongPip10Hz;
			public double SteerMinHz;
			public double BackwardMdnHz;

			public static Thresholds Default => new Thresholds {
				FeedMn9Hz = 35, // sugar drives MN9 ~57Hz; touch/bristles leak ~25Hz into it
				GroomADnHz = 40,
				SongPip10Hz = 15,
				SteerMinHz = 3,
				BackwardMdnHz = 20,
			};
		}

		/// <summary>The motor/descending populations Decode reads, in ladder order (labelled on the neuroscope).</summary>
		public static readonly (string type, string side)[] Readouts = {
			("DNp01", null), ("MN9", null), ("pIP10", null), ("DNg62", null), ("DNge078", null),
			("DNa02", "L"), ("DNa02", "R"), ("DNp09", null), ("DNg100", null), ("MDN", null),
		};

		private readonly LifNetwork _net;
		private readonly PopulationIndex _pops;
		private readonly Thresholds _t;

		public MotorDecoder(LifNetwork net, PopulationIndex pops, Thresholds t) => (_net, _pops, _t) = (net, pops, t);

		private double Rate(string type, string side = null) => _net.MeanRateHz(_pops.Resolve(type, side));

		public MotorCommand Decode(bool brainDriven)
		{
			var cmd = new MotorCommand { Mode = MoteMode.Idle, Reflex = !brainDriven };

			// ESCAPE: any giant-fiber spike event. Caller passes the spike flag via
			// DNp01 rate spiking this tick; threshold kept low on purpose.
			if (Rate("DNp01") > 1.0) {
				cmd.Mode = MoteMode.Escape;
				return cmd;
			}
			if (Rate("MN9") >= _t.FeedMn9Hz) {
				cmd.Mode = MoteMode.Feed;
				return cmd;
			}
			if (Rate("LC4") + Rate("LPLC2") > 60) {
				cmd.Mode = MoteMode.Startle;
				return cmd;
			}
			if (Rate("pIP10") >= _t.SongPip10Hz) {
				cmd.Mode = MoteMode.Song;
				return cmd;
			}
			double groom = Rate("DNg62") + Rate("DNge078");
			if (groom >= _t.GroomADnHz) {
				cmd.Mode = MoteMode.Groom;
				return cmd;
			}

			double yaw = Rate("DNa02", "R") - Rate("DNa02", "L");
			double fwd = Rate("DNp09") + Rate("DNg100") * 0.5;
			double back = Rate("MDN");
			if (back >= _t.BackwardMdnHz && back > fwd) {
				cmd.Mode = MoteMode.Follow;
				cmd.Forward = -1;
				return cmd;
			}
			// Yaw needs a real rate difference: the rate EMA decays toward 0 but never
			// reaches it, so "yaw != 0" would latch FOLLOW forever after one chase.
			if (fwd >= _t.SteerMinHz || System.Math.Abs(yaw) >= _t.SteerMinHz) {
				cmd.Mode = MoteMode.Follow;
				cmd.Forward = fwd >= _t.SteerMinHz ? 1 : 0;
				cmd.Yaw = (float)yaw;
			}
			return cmd;
		}
	}
}
