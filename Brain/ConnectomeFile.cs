using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FlyRarria.Brain
{
	/// <summary>
	/// Reads the whole-CNS connectome written by tools/extract_connectome.py
	/// (flyraria-connectome-v1, layout documented there): every male-cns neuron and
	/// every connection of at least <c>minWeight</c> synapses between two of them.
	/// </summary>
	public static class ConnectomeFile
	{
		public const string FileName = "male-cns.connectome.gz";
		private const string Magic = "FLYCONN1";

		/// <summary>
		/// Neurons in ascending bodyId order. Signs, types and SomaX follow the same rules as
		/// <see cref="CircuitLoader"/>; Positions has NaN for the few neurons neuPrint can't place.
		/// </summary>
		public static Connectome Read(Stream stream, out string dataset, out int minWeight)
		{
			if (!BitConverter.IsLittleEndian) {
				throw new PlatformNotSupportedException("connectome files are little-endian");
			}
			byte[] data;
			int length;
			using (var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
			using (var ms = new MemoryStream()) {
				gz.CopyTo(ms);
				data = ms.GetBuffer();
				length = (int)ms.Length;
			}
			var r = new Cursor(data, length);
			if (length < Magic.Length || Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic) {
				throw new InvalidDataException("not a flyraria-connectome-v1 file");
			}
			r.Pos = Magic.Length;

			dataset = r.String();
			minWeight = r.Int32();
			int n = r.Int32();
			int e = r.Int32();
			var strings = new string[r.Int32()];
			for (int i = 0; i < strings.Length; i++) {
				strings[i] = r.String();
			}

			int[] bodies = r.Int32s(n);
			int[] typeIndex = r.Int32s(n);
			int[] ntIndex = r.Int32s(n);
			r.Int32s(n); // superclass: not used yet
			var types = new string[n];
			var signs = new sbyte[n];
			var somaX = new double[n];
			for (int i = 0; i < n; i++) {
				types[i] = strings[typeIndex[i]];
				signs[i] = Connectome.SignForTransmitter(strings[ntIndex[i]]);
				byte side = data[r.Pos + i];
				somaX[i] = side == 'L' ? -1 : side == 'R' ? 1 : 0;
			}
			r.Pos += n;
			float[] positions = r.Floats(3 * n);

			var rowStart = new int[n];
			int acc = 0;
			for (int i = 0; i < n; i++) {
				rowStart[i] = acc;
				acc += r.Varint();
			}
			if (acc != e) {
				throw new InvalidDataException($"rows hold {acc} connections, header says {e}");
			}
			var targets = new int[e];
			for (int i = 0; i < n; i++) {
				int end = i + 1 < n ? rowStart[i + 1] : e;
				int target = 0;
				for (int k = rowStart[i]; k < end; k++) {
					target += r.Varint();
					if ((uint)target >= (uint)n) {
						throw new InvalidDataException($"connection {k} targets neuron {target} of {n}");
					}
					targets[k] = target;
				}
			}
			var counts = new ushort[e];
			for (int k = 0; k < e; k++) {
				counts[k] = (ushort)Math.Min(r.Varint(), ushort.MaxValue);
			}
			if (r.Pos != length) {
				throw new InvalidDataException($"{length - r.Pos} bytes left over");
			}

			return new Connectome(n, rowStart, targets, counts, signs, types, bodies, somaX, new double[n], positions);
		}

		private sealed class Cursor
		{
			private readonly byte[] _data;
			private readonly int _length;
			public int Pos;

			public Cursor(byte[] data, int length) => (_data, _length) = (data, length);

			private void Need(int bytes)
			{
				if (bytes < 0 || Pos + bytes > _length) {
					throw new InvalidDataException("connectome file is truncated");
				}
			}

			public int Int32()
			{
				Need(4);
				int v = BitConverter.ToInt32(_data, Pos);
				Pos += 4;
				return v;
			}

			public string String()
			{
				Need(2);
				int len = BitConverter.ToUInt16(_data, Pos);
				Pos += 2;
				Need(len);
				string s = Encoding.UTF8.GetString(_data, Pos, len);
				Pos += len;
				return s;
			}

			public int[] Int32s(int count)
			{
				Need(count * 4);
				var a = new int[count];
				Buffer.BlockCopy(_data, Pos, a, 0, count * 4);
				Pos += count * 4;
				return a;
			}

			public float[] Floats(int count)
			{
				Need(count * 4);
				var a = new float[count];
				Buffer.BlockCopy(_data, Pos, a, 0, count * 4);
				Pos += count * 4;
				return a;
			}

			public int Varint()
			{
				int result = 0;
				for (int shift = 0; shift < 35; shift += 7) {
					Need(1);
					byte b = _data[Pos++];
					result |= (b & 0x7F) << shift;
					if (b < 0x80) {
						return result;
					}
				}
				throw new InvalidDataException("varint longer than 5 bytes");
			}
		}
	}
}
