namespace FlyRarria.Brain
{
	/// <summary>
	/// Reads descending/motor population rates into a movement command.
	/// Priority ladder (first match wins): ESCAPE > FEED > STARTLE > SONG > GROOM > SEEK > FOLLOW > IDLE.
	/// Thresholds are hand-built scaffolding; the decisions (which populations
	/// fire) come from the wiring. Tune per circuit set in config, not in code.
	/// Rates near a threshold flicker tick to tick, so the decoder holds a mode with
	/// hysteresis and a minimum dwell rather than switching on every 50 ms reading.
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
		Seek,
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
		/// <summary>Mean food-odor (ORN) rate that turns the mote toward the stronger-smelling side.</summary>
		public double SeekOrnHz;
			/// <summary>The current mode keeps holding down to this fraction of its entry threshold.</summary>
			public double ReleaseFraction;
			/// <summary>Brain ticks in a row a new mode must win before the fly switches to it. ESCAPE switches at once.</summary>
			public int ConfirmTicks;
			/// <summary>Brain ticks a mode holds after it starts before anything but ESCAPE replaces it.</summary>
			public int MinDwellTicks;

			public static Thresholds Default => new Thresholds {
				FeedMn9Hz = 35, // sugar drives MN9 ~57Hz; touch/bristles leak ~25Hz into it
				GroomADnHz = 30, // strong wind alone reads ~35; rain/damage read 110+; other baselines read 0
				SongPip10Hz = 15,
			SeekOrnHz = 25, // odor at 60Hz drives ORNs ~70Hz; hunger-weighted full-fly ~11Hz stays out
				SteerMinHz = 3,
				BackwardMdnHz = 20,
				ReleaseFraction = 0.6,
				ConfirmTicks = 3, // 150 ms
				MinDwellTicks = 10, // 0.5 s
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
		private MoteMode _mode = MoteMode.Idle;
		private int _heldTicks;
		private MoteMode _pending;
		private int _pendingTicks;

		public MotorDecoder(LifNetwork net, PopulationIndex pops, Thresholds t) => (_net, _pops, _t) = (net, pops, t);

		private double Rate(string type, string side = null) => _net.MeanRateHz(_pops.Resolve(type, side));

		/// <summary>Reads this brain tick's rates and returns the held command. Call once per brain tick.</summary>
		public MotorCommand Decode(bool brainDriven)
		{
			MotorCommand read = Read(_mode);
			read.Reflex = !brainDriven;
			_heldTicks++;
			if (read.Mode == _mode) {
				_pendingTicks = 0;
				return read;
			}
			_pendingTicks = read.Mode == _pending ? _pendingTicks + 1 : 1;
			_pending = read.Mode;
			if (read.Mode == MoteMode.Escape
				|| (_heldTicks >= _t.MinDwellTicks && _pendingTicks >= _t.ConfirmTicks)) {
				_mode = read.Mode;
				_heldTicks = 0;
				_pendingTicks = 0;
				return read;
			}
		var held = new MotorCommand { Mode = _mode, Reflex = read.Reflex };
		if (_mode == MoteMode.Follow) {
			// TrySteer at release scale keeps real steering while the switch confirms;
			// on failure yaw stays 0 so EMA residue never steers the mote.
			TrySteer(ref held, _t.ReleaseFraction);
		}
		return held;
		}

		/// <summary>One tick's reading of the ladder; <paramref name="current"/> gets the lower release thresholds.</summary>
		private MotorCommand Read(MoteMode current)
		{
			double Scale(MoteMode m) => m == current ? _t.ReleaseFraction : 1;
			var cmd = new MotorCommand { Mode = MoteMode.Idle };

			// ESCAPE: any giant-fiber spike event; threshold kept low on purpose.
			if (Rate("DNp01") > 1.0 * Scale(MoteMode.Escape)) {
				cmd.Mode = MoteMode.Escape;
				return cmd;
			}
			if (Rate("MN9") >= _t.FeedMn9Hz * Scale(MoteMode.Feed)) {
				cmd.Mode = MoteMode.Feed;
				return cmd;
			}
		// STARTLE: looming seen without giant-fiber takeoff, or a small moving
		// thing (LC11) worth freezing to watch. LC11 never bursts DNp01, so
		// Escape can't shadow it the way it shadows the loom clause.
		if (Rate("LC4") + Rate("LPLC2") > 60 * Scale(MoteMode.Startle)
			|| Rate("LC11") > 40 * Scale(MoteMode.Startle)) {
			cmd.Mode = MoteMode.Startle;
			return cmd;
		}
			if (Rate("pIP10") >= _t.SongPip10Hz * Scale(MoteMode.Song)) {
				cmd.Mode = MoteMode.Song;
				return cmd;
			}
			double groom = Rate("DNg62") + Rate("DNge078");
			if (groom >= _t.GroomADnHz * Scale(MoteMode.Groom)) {
				cmd.Mode = MoteMode.Groom;
				return cmd;
			}
		// SEEK: hungry-weighted food odor with no contact yet — turn toward the
		// stronger side and close in. Smell drive already carries hunger (see
		// Encode), so a full fly never reaches this.
		double orn = (Rate("ORN_DM1") + Rate("ORN_VA2")) * 0.5;
		if (orn >= _t.SeekOrnHz * Scale(MoteMode.Seek)) {
			cmd.Mode = MoteMode.Seek;
			cmd.Forward = 1;
			cmd.Yaw = (float)(Rate("ORN_DM1", "R") + Rate("ORN_VA2", "R")
				- Rate("ORN_DM1", "L") - Rate("ORN_VA2", "L"));
			return cmd;
		}
			TrySteer(ref cmd, Scale(MoteMode.Follow));
			return cmd;
		}

		/// <summary>Sets FOLLOW with forward/yaw when steering rates clear <paramref name="scale"/> x their thresholds.</summary>
		private bool TrySteer(ref MotorCommand cmd, double scale)
		{
			double yaw = Rate("DNa02", "R") - Rate("DNa02", "L");
			double fwd = Rate("DNp09") + Rate("DNg100") * 0.5;
			double back = Rate("MDN");
			double steer = _t.SteerMinHz * scale;
			if (back >= _t.BackwardMdnHz * scale && back > fwd) {
				cmd.Mode = MoteMode.Follow;
				cmd.Forward = -1;
				return true;
			}
			// Yaw needs a real rate difference: the rate EMA decays toward 0 but never
			// reaches it, so "yaw != 0" would latch FOLLOW forever after one chase.
			if (fwd >= steer || System.Math.Abs(yaw) >= steer) {
				cmd.Mode = MoteMode.Follow;
				cmd.Forward = fwd >= steer ? 1 : 0;
				cmd.Yaw = (float)yaw;
				return true;
			}
			return false;
		}
	}
}
