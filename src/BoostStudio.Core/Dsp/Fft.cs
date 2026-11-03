namespace BoostStudio.Core.Dsp;

/// <summary>Small radix-2 FFT used by the live spectrum analyzer.</summary>
public static class Fft
{
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    public static void Transform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        if (n == 0) return;
        if (imaginary.Length != n) throw new ArgumentException("Real and imaginary buffers must be the same length.", nameof(imaginary));
        if (!IsPowerOfTwo(n)) throw new ArgumentException("FFT length must be a power of two.", nameof(real));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wLengthReal = Math.Cos(angle);
            var wLengthImag = Math.Sin(angle);

            for (var start = 0; start < n; start += length)
            {
                var wReal = 1.0;
                var wImag = 0.0;
                var half = length >> 1;

                for (var k = 0; k < half; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + half;

                    var oddReal = real[oddIndex] * wReal - imaginary[oddIndex] * wImag;
                    var oddImag = real[oddIndex] * wImag + imaginary[oddIndex] * wReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImag;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImag;

                    var nextWReal = wReal * wLengthReal - wImag * wLengthImag;
                    wImag = wReal * wLengthImag + wImag * wLengthReal;
                    wReal = nextWReal;
                }
            }
        }
    }
}
