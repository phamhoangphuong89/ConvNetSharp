﻿using System.Collections.Generic;
using ConvNetSharp.Flow.Serialization.Onnx;
using ConvNetSharp.Volume;
using NUnit.Framework;

namespace ConvNetSharp.Flow.Tests
{
    [TestFixture]
    public class OnnxLoadTest
    {
        /// <summary>
        /// Load ONNX model generated by pytorch (See ./Data/Onnx/python/train.py)
        /// </summary>
        [Test]
        public void Load()
        {
            var loader = new OnnxLoader<float>("./Data/Onnx/mnist.onnx");
            var ops = loader.Load();

            var input = ops["input"];
            var output = ops["output"];

            using var session = new Session<float>();
            var dico = new Dictionary<string, Volume<float>> { { "input", BuilderInstance<float>.Volume.Random(Shape.From(28, 28, 1, 2)) } };
            var result = session.Run(output, dico);
        }
    }
}