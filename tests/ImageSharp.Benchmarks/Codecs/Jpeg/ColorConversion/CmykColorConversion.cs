// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Formats.Jpeg.Components.Decoder.ColorConverters;

namespace SixLabors.ImageSharp.Benchmarks.Codecs.Jpeg
{
    [Config(typeof(Config.ShortMultiFramework))]
    public class CmykColorConversion : ColorConversionBenchmark
    {
        public CmykColorConversion()
            : base(4)
        {
        }

        [Benchmark(Baseline = true)]
        public void Scalar()
        {
            var values = new JpegColorConverterBase.ComponentValues(this.Input, 0);

            new JpegColorConverterBase.FromCmykScalar(8).ConvertToRgbInplace(values);
        }

        [Benchmark]
        public void SimdVector8()
        {
            var values = new JpegColorConverterBase.ComponentValues(this.Input, 0);

            new JpegColorConverterBase.FromCmykVector8(8).ConvertToRgbInplace(values);
        }

#if SUPPORTS_RUNTIME_INTRINSICS
        [Benchmark]
        public void SimdVectorAvx()
        {
            var values = new JpegColorConverterBase.ComponentValues(this.Input, 0);

            new JpegColorConverterBase.FromCmykAvx(8).ConvertToRgbInplace(values);
        }
#endif
    }
}
