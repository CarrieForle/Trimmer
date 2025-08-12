using System.Text.RegularExpressions;

namespace Trimmer.Trimmer
{
    partial class Timecode
    {
        private string timecode;
        private static readonly Timecode zero = new(0, 0, 0);
        private static readonly Timecode end = new(int.MaxValue, int.MaxValue, double.MaxValue);

        private string GenerateTimecodeString()
        {
            return $"{Hour:D2}:{Minute:D2}:{Second:00.######}";
        }

        private Timecode(int hour, int minute, double second)
        {
            Hour = hour;
            Minute = minute;
            Second = second;
            timecode = GenerateTimecodeString();
        }

        public Timecode(string timecode)
        {
            var match = TimecodeRegex().Match(timecode);

            if (!match.Success)
            {
                throw new Exception("Invalid timecode.");
            }
            
            List<int> parts = [0, 0];

            for (int i = 0; i < 2; i++)
            {
                if (match.Groups[i + 1].Success)
                {
                    parts[i] = int.Parse(match.Groups[i + 1].ToString());
                }
            }

            // If minute is present in timecode
            if (match.Groups[2].Success)
            {
                Hour = parts[0];
                Minute = parts[1];
            }
            else
            {
                Hour = 0;
                Minute = parts[0];
            }

            if (match.Groups[3].Success)
            {
                Second = double.Parse(match.Groups[3].ToString());
            }
            else
            {
                Second = 0;
            }

            this.timecode = GenerateTimecodeString();
        }

        private int Hour { get; init; }
        private int Minute { get; init; }
        private double Second { get; init; }

        public static Timecode Zero()
        {
            return zero;
        }

        /// <summary>
        /// Represent end of video. This is useful
        /// for ffmpeg commands that don't trim end.
        /// 
        /// Calling <c>ToString()</c> on this throws
        /// an exception.
        /// </summary>
        public static Timecode End()
        {
            return end;
        }

        public static Timecode operator -(Timecode a, Timecode b)
        {
            if (a < b)
            {
                return Zero();
            }

            int hour = a.Hour - b.Hour;
            int minute = a.Minute + b.Minute;
            double second = a.Second + b.Second;

            if (second >= 60)
            {
                minute += 1;
                second -= 60;
            }
            if (minute >= 60)
            {
                hour += 1;
                minute -= 60;
            }

            return new Timecode(hour, minute, second);
        }

        public static bool operator >(Timecode a, Timecode b)
        {
            long min1 = (long)a.Hour + a.Minute;
            long min2 = (long)b.Hour + b.Minute;

            if (min1 > min2)
            {
                return true;
            }

            if (min1 == min2)
            {
                return a.Second > b.Second;
            }

            return false;
        }

        public static bool operator <(Timecode a, Timecode b)
        {
            long min1 = (long)a.Hour + a.Minute;
            long min2 = (long)b.Hour + b.Minute;

            if (min1 < min2)
            {
                return true;
            }

            if (min1 == min2)
            {
                return a.Second < b.Second;
            }

            return false;
        }

        public static bool operator >=(Timecode a, Timecode b)
        {
            return !(a < b);
        }

        public static bool operator <=(Timecode a, Timecode b)
        {
            return !(a > b);
        }

        public override string ToString()
        {
            if (this == end)
            {
                // throw new InvalidOperationException("Cannot call this on End Timecode.");
                return "Timecode.End";
            }

            return timecode;
        }

        /// <summary> 
        /// Note: The returned Timecode might not 
        /// be accurate to second due to rounding 
        /// error. Try not to pass a value too big.
        /// 
        /// As a baseline, <c>second</c> must not be greater
        /// than <c>int.MaxValue</c>.
        /// </summary> 
        public static Timecode OfSecond(double second)
        {
            if (second < 0)
            {
                throw new ArgumentException("Second is negative.");
            }

            if (second > int.MaxValue)
            {
                throw new ArgumentException("Second exceeds int maximum.");
            }

            int sec_int = (int)second;

            // For good measure. I have no idea if
            // it's possible to go down this path.
            if (sec_int < 0)
            {
                throw new ArgumentException("Rounding error.");
            }

            int hour = sec_int / 3600;
            int minute = sec_int / 60 % 60;
            second %= 60;

            return new Timecode(hour, minute, second);
        }

        public static Timecode OfSecond(int second)
        {
            if (second < 0)
            {
                throw new ArgumentException("Second is negative.");
            }

            int hour = second / 3600;
            int minute = second / 60 % 60;
            second %= 60;

            return new Timecode(hour, minute, second);
        }

        [GeneratedRegex(@"^(?:([0-9]{1,2}):)?(?:([0-5]?[0-9]):)?((?:[0-5]?[0-9])(?:\.[0-9]{1,6})?)$")]
        private static partial Regex TimecodeRegex();
    }
}