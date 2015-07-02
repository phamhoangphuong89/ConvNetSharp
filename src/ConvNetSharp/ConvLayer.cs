﻿using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace ConvNetSharp
{
    [DataContract]
    public class ConvLayer : LayerBase, IDotProductLayer
    {
        public ConvLayer(int width, int height, int filterCount)
        {
            this.GroupSize = 2;
            this.L1DecayMul = 0.0;
            this.L2DecayMul = 1.0;
            this.Stride = 1;
            this.Pad = 0;

            this.FilterCount = filterCount;
            this.Width = width;
            this.Height = height;
        }

        [DataMember]
        public Volume Biases { get; private set; }

        [DataMember]
        public List<Volume> Filters { get; private set; }

        [DataMember]
        public int FilterCount { get; private set; }

        [DataMember]
        public double L1DecayMul { get; set; }

        [DataMember]
        public double L2DecayMul { get; set; }

        [DataMember]
        public int Stride { get; set; }

        [DataMember]
        public int Pad { get; set; }

        [DataMember]
        public double BiasPref { get; set; }

        [DataMember]
        public Activation Activation { get; set; }

        [DataMember]
        public int GroupSize { get; private set; }

        public override Volume Forward(Volume volume, bool isTraining = false)
        {
            // optimized code by @mdda that achieves 2x speedup over previous version

            this.InputActivation = volume;
            var outputActivation = new Volume(this.OutputWidth, this.OutputHeight, this.OutputDepth, 0.0);

            var volumeWidth = volume.Width;
            var volumeHeight = volume.Height;
            var xyStride = this.Stride;

#if PARALLEL
            Parallel.For(0, this.OutputDepth, depth =>
#else
            for (var depth = 0; depth < this.OutputDepth; depth++)
#endif
            {
                var filter = this.Filters[depth];
                var x = -this.Pad;
                var y = -this.Pad;

                for (var ay = 0; ay < this.OutputHeight; y += xyStride, ay++)
                {
                    // xyStride
                    x = -this.Pad;
                    for (var ax = 0; ax < this.OutputWidth; x += xyStride, ax++)
                    {
                        // xyStride

                        // convolve centered at this particular location
                        var a = 0.0;
                        for (var fy = 0; fy < filter.Height; fy++)
                        {
                            var oy = y + fy; // coordinates in the original input array coordinates
                            for (var fx = 0; fx < filter.Width; fx++)
                            {
                                var ox = x + fx;
                                if (oy >= 0 && oy < volumeHeight && ox >= 0 && ox < volumeWidth)
                                {
                                    for (var fd = 0; fd < filter.Depth; fd++)
                                    {
                                        // avoid function call overhead (x2) for efficiency, compromise modularity :(
                                        a += filter.Weights[((filter.Width * fy) + fx) * filter.Depth + fd] *
                                             volume.Weights[((volumeWidth * oy) + ox) * volume.Depth + fd];
                                    }
                                }
                            }
                        }

                        a += this.Biases.Weights[depth];
                        outputActivation.Set(ax, ay, depth, a);
                    }
                }
            }
#if PARALLEL
);
#endif

            this.OutputActivation = outputActivation;
            return this.OutputActivation;
        }

        public override void Backward()
        {
            var volume = this.InputActivation;
            volume.WeightGradients = new double[volume.Weights.Length]; // zero out gradient wrt bottom data, we're about to fill it

            var volumeWidth = volume.Width;
            var volumeHeight = volume.Height;
            var xyStride = this.Stride;

            for (var depth = 0; depth < this.OutputDepth; depth++)
            {
                var filter = this.Filters[depth];
                var x = -this.Pad;
                var y = -this.Pad;
                for (var ay = 0; ay < this.OutputHeight; y += xyStride, ay++)
                {
                    // xyStride
                    x = -this.Pad;
                    for (var ax = 0; ax < this.OutputWidth; x += xyStride, ax++)
                    {
                        // xyStride

                        // convolve centered at this particular location
                        var chainGradient = this.OutputActivation.GetGradient(ax, ay, depth);
                        // gradient from above, from chain rule
                        for (var fy = 0; fy < filter.Height; fy++)
                        {
                            var oy = y + fy; // coordinates in the original input array coordinates
                            for (var fx = 0; fx < filter.Width; fx++)
                            {
                                var ox = x + fx;
                                if (oy >= 0 && oy < volumeHeight && ox >= 0 && ox < volumeWidth)
                                {
                                    for (var fd = 0; fd < filter.Depth; fd++)
                                    {
                                        filter.AddGradient(fx, fy, fd, volume.Get(ox, oy, fd) * chainGradient);
                                        volume.AddGradient(ox, oy, fd, filter.Get(fx, fy, fd) * chainGradient);
                                    }
                                }
                            }
                        }

                        this.Biases.WeightGradients[depth] += chainGradient;
                    }
                }
            }
        }

        public override void Init(int inputWidth, int inputHeight, int inputDepth)
        {
            base.Init(inputWidth, inputHeight, inputDepth);

            // required
            this.OutputDepth = this.FilterCount;

            // computed
            // note we are doing floor, so if the strided convolution of the filter doesnt fit into the input
            // volume exactly, the output volume will be trimmed and not contain the (incomplete) computed
            // final application.
            this.OutputWidth = (int)Math.Floor((this.InputWidth + this.Pad * 2 - this.Width) / (double)this.Stride + 1);
            this.OutputHeight = (int)Math.Floor((this.InputHeight + this.Pad * 2 - this.Height) / (double)this.Stride + 1);

            // initializations
            var bias = this.BiasPref;
            this.Filters = new List<Volume>();

            for (var i = 0; i < this.OutputDepth; i++)
            {
                this.Filters.Add(new Volume(this.Width, this.Height, this.InputDepth));
            }

            this.Biases = new Volume(1, 1, this.OutputDepth, bias);
        }

        public override List<ParametersAndGradients> GetParametersAndGradients()
        {
            var response = new List<ParametersAndGradients>();
            for (var i = 0; i < this.OutputDepth; i++)
            {
                response.Add(new ParametersAndGradients
                {
                    Parameters = this.Filters[i].Weights,
                    Gradients = this.Filters[i].WeightGradients,
                    L2DecayMul = this.L2DecayMul,
                    L1DecayMul = this.L1DecayMul
                });
            }

            response.Add(new ParametersAndGradients
            {
                Parameters = this.Biases.Weights,
                Gradients = this.Biases.WeightGradients,
                L1DecayMul = 0.0,
                L2DecayMul = 0.0
            });

            return response;
        }
    }
}