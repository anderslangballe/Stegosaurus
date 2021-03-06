﻿using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Stegosaurus.Utility;
using System.ComponentModel;
using System.Threading;
using Stegosaurus.Algorithm.GraphTheory;
using Stegosaurus.Exceptions;

namespace Stegosaurus.Algorithm
{
    public class GraphTheoreticAlgorithm : StegoAlgorithmBase
    {
        public override string Name => "Graph Theoretic Algorithm";

        protected override byte[] Signature => new byte[] { 0x47, 0x54, 0x41, 0x6C };

        private byte samplesPerVertex = 2;
        [Category("Algorithm"), Description("The number of samples collected in each vertex. Higher numbers means less bandwidth but more imperceptibility.(Default = 2, Max = 4.)")]
        public byte SamplesPerVertex
        {
            get { return samplesPerVertex; }
            set { samplesPerVertex = (value <= 4) ? ((value >= 1) ? value : (byte)1) : (byte)4; }
        }

        private byte messageBitsPerVertex = 2;
        [Category("Algorithm"), Description("The number of bits hidden in each vertex. Higher numbers means more bandwidth but less imperceptibility.(Default = 2, Max = 4.)")]
        public byte MessageBitsPerVertex
        {
            get { return messageBitsPerVertex; }
            set
            {
                byte temp = (byte)(1 << ((int)Math.Log(value, 2)));
                messageBitsPerVertex = (temp <= 4) ? ((temp >= 1) ? temp : (byte)1) : (byte)4;
            }
        }

        private int distanceMax = 8;
        [Category("Algorithm"), Description("The maximum distance between single samplevalues for an edge to be valid. Higher numbers means less visual imperceptibility but more statistical imperceptibility. Higher numbers might also decrease performance, depending on DistancePrecision. (Default = 32, Min-Max = 2-128.)")]
        public int DistanceMax
        {
            get { return distanceMax; }
            set { distanceMax = (value <= 128) ? ((value >= 2) ? value : 2) : 128; }
        }

        private int distancePrecision = 2;
        [Category("Algorithm"), Description("The distance precision. Higher numbers significantly decreases performance with high DistanceMax. (Default = 8, Min-Max = 2-32.)")]
        public int DistancePrecision
        {
            get { return 1 << distancePrecision; }
            set { distancePrecision = (value <= 32) ? ((value >= 0) ? (int)Math.Log(value, 2) : 0) : 32; }
        }

        private int verticesPerMatching = 50000;
        [Category("Algorithm"), Description("The maximum number of vertices to find edges for at a time. Higher numbers means more memory usage but better imperceptibility. (Default = 150,000, Min = 10,000.)")]
        public int VerticesPerMatching
        {
            get { return verticesPerMatching; }
            set { verticesPerMatching = (value >= 10000) ? value : 10000; }
        }

        private int reserveMatching = 1;
        [Category("Algorithm"), Description("The number of times to try matching leftover vertices with reserve samples. (Default = 2, Min-Max = 0-8.)")]
        public int ReserveMatching
        {
            get { return reserveMatching; }
            set { reserveMatching = (value <= 8) ? ((value >= 0) ? value : 0) : 8; }
        }

        private OptionPresets currentPreset = OptionPresets.Default;
        [Category("Algorithm"), Description("The preset settings, which affects the overall speed and quality of the embedding process.")]
        public OptionPresets Preset
        {
            get
            {
                return currentPreset;
            }
            set
            {
                switch (value)
                {
                    case OptionPresets.Default:
                        verticesPerMatching = 50000;
                        distancePrecision = 2;
                        distanceMax = 8;
                        messageBitsPerVertex = 2;
                        samplesPerVertex = 2;
                        break;
                    case OptionPresets.Imperceptibility:
                        verticesPerMatching = 100000;
                        //distancePrecision = 1;
                        //distanceMax = 4;
                        break;
                    case OptionPresets.Performance:
                        verticesPerMatching = 10000;
                        //distancePrecision = 4;
                        //distanceMax = 8;
                        break;
                }

                currentPreset = value;
            }
        }

        private int progress, progressCounter, progressUpdateInterval;
        private byte modFactor;
        private byte bitwiseModFactor;

        public override long ComputeBandwidth()
        {
            return ((((CarrierMedia.ByteArray.Length / CarrierMedia.BytesPerSample) / samplesPerVertex) * messageBitsPerVertex) / 8) - Signature.Length;
        }

        #region Embed
        public override void Embed(StegoMessage _message, IProgress<int> _progress, CancellationToken _ct)
        {
            modFactor = (byte)(1 << messageBitsPerVertex);
            bitwiseModFactor = (byte)(modFactor - 1);
            progress = 0;
            progressCounter = 0;

            // Verify that the carrier is supported by the algorithm.
            if (CarrierMedia.BytesPerSample != 3)
            {
                throw new StegoAlgorithmException("The selected carrier is not supported by this algorithm.");
            }

            // Convert StegoMessage into chunks of bytes.
            List<byte> messageChunks = GetMessageChunks(_message);//GetMessage(_message, _progress, _ct, 10);

            // Convert bytes CarrierMedia to a list of Samples.
            List<Sample> sampleList = Sample.GetSampleListFrom(CarrierMedia, bitwiseModFactor);

            // Get Vertex lists.
            Tuple<List<Vertex>, List<Vertex>> verticeTuple = GetVerticeLists(sampleList, messageChunks);
            List<Vertex> messageVertexList = verticeTuple.Item1;
            List<Vertex> reserveVertexList = verticeTuple.Item2;
            int messageVertexCount = messageVertexList.Count;

            // Find and swap edges.
            // Returned value is a list of vertices that could not be changed.
            List<Vertex> unmatchedVertexList = FindEdgesAndSwap(messageVertexList, _progress, _ct, 100);

            unmatchedVertexList = DoReserveMatching(unmatchedVertexList, reserveVertexList, _progress, _ct);
            // Adjust vertices that could not be swapped.
            Adjust(unmatchedVertexList);
            Console.WriteLine("Adjusted {0} vertices ({1}% of total).", unmatchedVertexList.Count, (unmatchedVertexList.Count / (float)messageVertexCount) * 100);

            // Finally encode the samples back into the CarrierMedia.
            Encode(sampleList);
            _progress?.Report(100);
        }

        /// <summary>
        /// Encodes the Sample.Values bytes back into the CarrierMedia
        /// </summary>
        private void Encode(List<Sample> _sampleList)
        {
            int pos = 0;
            foreach (byte sample in _sampleList.SelectMany(current => current.Values))
            {
                CarrierMedia.ByteArray[pos++] = sample;
            }
        }

        /// <summary>
        /// Adjust a list of vertices, so all vertices have their target value.
        /// There is a randomly selected sample and byteIndex that will be edited for each vertex.
        /// </summary>
        private void Adjust(List<Vertex> _vertices)
        {
            Random rand = new Random();

            foreach (Vertex vertex in _vertices)
            {
                int sampleIndex = rand.Next(SamplesPerVertex), byteIndex = rand.Next(CarrierMedia.BytesPerSample);

                // Calculate difference.
                byte valueDifference = (byte)((modFactor - vertex.Samples[sampleIndex].ModValue + vertex.Samples[sampleIndex].TargetModValue) & bitwiseModFactor);

                // Adjust value.
                byte currentValue = vertex.Samples[sampleIndex].Values[byteIndex];
                if ((currentValue + valueDifference) <= byte.MaxValue)
                {
                    vertex.Samples[sampleIndex].Values[byteIndex] += valueDifference;
                }
                else
                {
                    vertex.Samples[sampleIndex].Values[byteIndex] -= (byte)(modFactor - valueDifference);
                }
            }
        }

        /// <summary>
        /// Returns a Tuple containing two lists of vertices.
        /// The first list contains the vertices that have been assigned a target value.
        /// The second list contains vertices with no targets, that can be used as a reserve to exchange.
        /// </summary>
        private Tuple<List<Vertex>, List<Vertex>> GetVerticeLists(List<Sample> _sampleList, List<byte> _messageChunks)
        {
            // Find the total amount of vertices in selected carrier.
            int totalNumVertices = _sampleList.Count / SamplesPerVertex;

            // Allocate memory for vertices to contain messages.
            // May allocate more memory than necessary, since some vertices already have the correct value.
            List<Vertex> messageVertices = new List<Vertex>(_messageChunks.Count);
            List<Vertex> reserveVertices = new List<Vertex>(totalNumVertices - _messageChunks.Count);

            // Iterate through the amount of items to generate.
            RandomNumberList randomNumbers = new RandomNumberList(Seed, _sampleList.Count);
            for (int numVertex = 0; numVertex < totalNumVertices; numVertex++)
            {
                Sample[] tmpSampleArray = new Sample[SamplesPerVertex];

                // Generate SamplesPerVertex items.
                for (int sampleIndex = 0; sampleIndex < SamplesPerVertex; sampleIndex++)
                {
                    tmpSampleArray[sampleIndex] = _sampleList[randomNumbers.Next];
                }

                // Calculate mod value of vertex.
                byte vertexModValue = (byte)(tmpSampleArray.Sum(val => val.ModValue) & bitwiseModFactor);

                // If index is more or equal to amount of message, add to reserves.
                // Otherwise add to message vertices.
                if (numVertex >= _messageChunks.Count)
                {
                    Vertex reserveVertex = new Vertex(tmpSampleArray) { Value = vertexModValue };
                    reserveVertices.Add(reserveVertex);
                }
                else
                {
                    Vertex messageVertex = new Vertex(tmpSampleArray) { Value = vertexModValue };
                    messageVertices.Add(messageVertex);

                    // Calculate delta value.
                    byte deltaValue = (byte)((modFactor + _messageChunks[numVertex] - messageVertex.Value) & bitwiseModFactor);

                    // Set target values.
                    foreach (Sample sample in messageVertex.Samples)
                    {
                        sample.TargetModValue = (byte)((sample.ModValue + deltaValue) & bitwiseModFactor);
                    }
                }
            }

            return new Tuple<List<Vertex>, List<Vertex>>(messageVertices, reserveVertices);
        }

        /// <summary>
        /// Finds edges for all vertices and applies these edges as possible. If the number of vertices exceeds the VerticesPerMatching limit it splits the list before handling each part seperately.
        /// Returns list of vertices which couldn't be swapped.
        /// </summary>
        private List<Vertex> FindEdgesAndSwap(List<Vertex> _vertices, IProgress<int> _progress, CancellationToken _ct, int _progressWeight)
        {
            int numRounds = (int)Math.Ceiling((decimal)_vertices.Count / VerticesPerMatching), roundProgressWeight = _progressWeight / numRounds;
            roundProgressWeight = roundProgressWeight == 0 ? 1 : roundProgressWeight;
            int verticesPerRound = _vertices.Count / numRounds + 1, maxCarryoverPerRound = VerticesPerMatching / 4;
            int verticeOffset = 0, startNumVertices = _vertices.Count;
            List<Vertex> leftoverVertexList = new List<Vertex>();

            // Continue until we have gone through all vertices.
            while (verticeOffset < startNumVertices)
            {
                //Console.WriteLine("Round: {0} ({1}/{2})", ++roundNumber, verticeOffset, startNumVertices);

                // Calculate how many vertices to use this round.
                int verticesThisRound = verticesPerRound > _vertices.Count ? _vertices.Count : verticesPerRound;
                verticeOffset += verticesThisRound;

                // Take this amount of vertices.
                List<Vertex> tmpVertexList = _vertices.GetRange(0, verticesThisRound);

                // Remove them from the main list.
                _vertices.RemoveRange(0, verticesThisRound);

                // Calculate how many leftover vertices to carry over.
                int leftoverCarryover = maxCarryoverPerRound > leftoverVertexList.Count ? leftoverVertexList.Count : maxCarryoverPerRound;

                // Add leftover vertices to tmpVertexList.
                tmpVertexList.AddRange(leftoverVertexList.GetRange(0, leftoverCarryover));

                // Remove the transfered vertices from the list.
                leftoverVertexList.RemoveRange(0, leftoverCarryover);

                List<Tuple<int, byte>>[,,,,] locationArray = GetArray(tmpVertexList, (byte.MaxValue >> distancePrecision) + 1);
                // Get edges for subset.
                GetEdges(tmpVertexList, locationArray, _progress, _ct, roundProgressWeight);

                // Swap edges found for subset and add leftovers to list.
                leftoverVertexList.AddRange(Swap(tmpVertexList, tmpVertexList));

                // Clear edges for subset.
                tmpVertexList.ForEach(v => v.Edges.Clear());
            }


            return leftoverVertexList;
        }
        
        /// <summary>
        /// Creates a 5 dimensional array and populates it with lists of vertice references.
        /// </summary>
        private List<Tuple<int, byte>>[,,,,] GetArray(List<Vertex> _vertices, int _dimensionSize)
        {
            List<Tuple<int, byte>>[,,,,] array = new List<Tuple<int, byte>>[_dimensionSize, _dimensionSize, _dimensionSize, modFactor, modFactor];
            int numVertices = _vertices.Count;
            
            for (int vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
            {
                for (byte sampleIndex = 0; sampleIndex < samplesPerVertex; sampleIndex++)
                {
                    Sample sample = _vertices[vertexIndex].Samples[sampleIndex];
                    // Create a tuple identifying the current vertex and sample.
                    Tuple<int, byte> vertexRef = Tuple.Create(vertexIndex, sampleIndex);

                    // Get the list of vertex references at the current samples location.
                    List<Tuple<int, byte>> vertexRefs = array[sample.Values[0] >> distancePrecision, sample.Values[1] >> distancePrecision, sample.Values[2] >> distancePrecision, sample.ModValue, sample.TargetModValue];
                    if (vertexRefs != null)
                    {
                        // If the list exists, add the vertexRef to the list.
                        vertexRefs.Add(vertexRef);
                    }
                    else
                    {
                        // If the list does not exist, instantiate a new list and add the vertexRef to the list.
                        array[sample.Values[0] >> distancePrecision, sample.Values[1] >> distancePrecision, sample.Values[2] >> distancePrecision, sample.ModValue, sample.TargetModValue] = new List<Tuple<int, byte>>() {vertexRef};
                    }
                }
            }
            return array;
        }

        /// <summary>
        /// Finds the edges for all the provided vertices.
        /// </summary>
        private void GetEdges(List<Vertex> _vertexList, List<Tuple<int, byte>>[,,,,] array, IProgress<int> _progress, CancellationToken _ct, int _progressWeight)
        {
            //Console.WriteLine($"Debug GetEdges:");
            int numVertices = _vertexList.Count;

            // Calculate the maximum values for the Sample.Values and neighborhood distance with DistancePrecision applied.
            byte dimMax = (byte)(byte.MaxValue >> distancePrecision), maxDelta = (byte)(distanceMax >> distancePrecision);
            
            int bytesPerSample = CarrierMedia.BytesPerSample;
            int[] minValues = new int[bytesPerSample], maxValues = new int[bytesPerSample];
            byte[] bestSwaps = new byte[2];

            progressCounter = 1;
            progressUpdateInterval = numVertices / _progressWeight;

            // Iterate through all vertices.
            for (int numVertex = 0; numVertex < numVertices; numVertex++, progressCounter++)
            {
                _ct.ThrowIfCancellationRequested();

                // Set the current vertex.
                Vertex vertex = _vertexList[numVertex];

                // Iterate through each of its samples.
                for (byte sampleIndex = 0; sampleIndex < samplesPerVertex; sampleIndex++)
                {
                    // Set the current sample values.
                    Sample outerSample = vertex.Samples[sampleIndex];
                    byte[] outerSampleValues = outerSample.Values;
                    byte sampleTargetValue = outerSample.TargetModValue;
                    byte sampleModValue = outerSample.ModValue;
                    bestSwaps[0] = sampleIndex;

                    // Calculate the neighborhood limits.
                    for (int byteIndex = 0; byteIndex < bytesPerSample; byteIndex++)
                    {
                        int temp = outerSampleValues[byteIndex] >> distancePrecision;
                        // minValues only applicable for before the second iteration of the Y-dimension.
                        minValues[byteIndex] = temp;
                        maxValues[byteIndex] = (temp + maxDelta) > dimMax ? dimMax : (temp + maxDelta);
                    }

                    // Ready bool check to alter the minValues for subsequent iterations.
                    bool firstXY = true;
                    // Ready bool check used when searching the current samples location.
                    bool isHere = true;
                    
                    // Iterate through the neighborhood dimensions.
                    for (int x = minValues[0]; x <= maxValues[0]; x++)
                    {
                        for (int y = minValues[1]; y <= maxValues[1]; y++)
                        {
                            for (int z = minValues[2]; z <= maxValues[2]; z++)
                            {
                                // Get the list of vertice references that lies at the current location, with the correct sampleTargetValue and sampleModValue.
                                List<Tuple<int, byte>> vertexRefs = array[x, y, z, sampleTargetValue, sampleModValue];
                                if (vertexRefs != null)
                                {
                                    // If the list exists, create edges between current vertex and all vertices referenced by the list.
                                    foreach (Tuple<int, byte> vertexRef in vertexRefs)
                                    {
                                        // When checking the current samples location, dont create an edge with a vertex we have already found edges for.
                                        if (isHere && vertexRef.Item1 <= numVertex)
                                        {
                                            continue;
                                        }

                                        // Get the info from the vertexRef tuple.
                                        Sample innerSample = _vertexList[vertexRef.Item1].Samples[vertexRef.Item2];
                                        bestSwaps[1] = vertexRef.Item2;

                                        // Calculate the exact distance between the outer and inner sample.
                                        // This value is used as the weight between two vertices.
                                        short distance = outerSample.DistanceTo(innerSample);

                                        // Create the new edge and add it to each vertex it applies to.
                                        Edge newEdge = new Edge(numVertex, vertexRef.Item1, distance, bestSwaps);
                                        foreach (int vertexId in newEdge.Vertices)
                                        {
                                            _vertexList[vertexId].Edges.Add(newEdge);
                                        }
                                    }
                                }

                                // Disable the vertex id check.
                                isHere = false;
                            }

                            // In the first iteration only, set the minValues for subsequent iterations.
                            if (firstXY)
                            {
                                minValues[1] = outerSampleValues[1] > distanceMax ? (outerSampleValues[1] - distanceMax) >> distancePrecision : 0;
                                minValues[2] = outerSampleValues[2] > distanceMax ? (outerSampleValues[2] - distanceMax) >> distancePrecision : 0;
                                firstXY = false;
                            }
                        }
                    }
                }

                // Update progress counter.
                if (progressCounter == progressUpdateInterval)
                {
                    progressCounter = 1;
                    _progress?.Report(++progress);
                    //Console.WriteLine($"... {numVertex} of {numVertices} handled. {(decimal)numVertex / numVertices:p}");
                }
            }
            //Console.WriteLine("GetEdges: Successful.");
        }

        /// <summary>
        /// Applies sample value swaps for the provided vertices, starting with the vertice with least edges.
        /// </summary>
        private List<Vertex> Swap(List<Vertex> _vertexList, List<Vertex> _otherVertexList)
        {
            List<Vertex> leftoverVertexList = new List<Vertex>();

            // Sort input list of vertices by amount of edges.
            List<Vertex> sortedVertexList = _vertexList.Select(x => x).ToList();
            sortedVertexList.Sort((v1, v2) => v1.Edges.Count - v2.Edges.Count);

            // Iterate through all vertices.
            foreach (Vertex vertex in sortedVertexList)
            {
                // Only swap valid vertices.
                if (vertex.IsValid)
                {
                    bool swapped = false;

                    // Sort current vertex edges by weight.
                    vertex.Edges.Sort((e1, e2) => e1.Weight - e2.Weight);

                    // Iterate through edges of this vertex.
                    foreach (Edge edge in vertex.Edges)
                    {
                        Vertex firstVertex = _vertexList[edge.Vertices[0]];
                        Vertex secondVertex = _otherVertexList[edge.Vertices[1]];

                        // Skip if either vertex is invalid.
                        if (firstVertex == secondVertex || !firstVertex.IsValid || !secondVertex.IsValid)
                        {
                            continue;
                        }

                        // Swap samples.
                        Sample firstSample = firstVertex.Samples[edge.BestSwaps[0]];
                        Sample secondSample = secondVertex.Samples[edge.BestSwaps[1]];
                        firstSample.Swap(secondSample);

                        // Disable vertices.
                        firstVertex.IsValid = false;
                        secondVertex.IsValid = false;

                        swapped = true;
                        break;
                    }

                    // Add to unmatched if it could not be swapped.
                    if (!swapped)
                    {
                        leftoverVertexList.Add(vertex);
                    }
                }
            }

            return leftoverVertexList;
        }


        private List<Vertex> DoReserveMatching(List<Vertex> _leftoverVertices, List<Vertex> _reserveVertices, IProgress<int> _progress, CancellationToken _ct)
        {
            int numRounds = (int)Math.Ceiling(((decimal)_leftoverVertices.Count / VerticesPerMatching) / 2);
            List<Vertex> leftoverVertexList = new List<Vertex>();
            int numIterations = 0;
            int verticesPerRound = _leftoverVertices.Count / numRounds + 1, maxCarryoverPerRound = VerticesPerMatching / 2;
            while (_leftoverVertices.Count > 0 && numIterations < reserveMatching)
            {
                // Calculate how many vertices to use this round.
                int verticesThisRound = verticesPerRound > _leftoverVertices.Count ? _leftoverVertices.Count : verticesPerRound;

                // Take this amount of vertices.
                List<Vertex> tmpVertexList = _leftoverVertices.GetRange(0, verticesThisRound);

                // Remove them from the main list.
                _leftoverVertices.RemoveRange(0, verticesThisRound);

                // Calculate how many leftover vertices to carry over.
                int leftoverCarryover = maxCarryoverPerRound > leftoverVertexList.Count ? leftoverVertexList.Count : maxCarryoverPerRound;

                // Add leftover vertices to tmpVertexList.
                tmpVertexList.AddRange(leftoverVertexList.GetRange(0, leftoverCarryover));

                // Remove the transfered vertices from the list.
                leftoverVertexList.RemoveRange(0, leftoverCarryover);

                //// Calculate how many vertices to use this round.
                //int reservesThisRound = VerticesPerMatching > _reserveVertices.Count ? _reserveVertices.Count : VerticesPerMatching;

                //// Take this amount of vertices.
                //List<Vertex> tmpReserveVertexList = _reserveVertices.GetRange(0, reservesThisRound);

                //// Remove them from the main list.
                //_reserveVertices.RemoveRange(0, verticesThisRound);

                // Get Location Array for reserve vertices.
                List<Tuple<int, byte>>[,,,,] locationArray = GetArray(_reserveVertices, (byte.MaxValue >> distancePrecision) + 1);

                // Get edges for subset.
                GetReserveEdges(tmpVertexList, _reserveVertices, locationArray, _progress, _ct, 1);

                // Swap edges found for subset and add leftovers to list.
                leftoverVertexList.AddRange(Swap(tmpVertexList, _reserveVertices));

                // Clear edges for subset.
                tmpVertexList.ForEach(v => v.Edges.Clear());
            }

            return leftoverVertexList;
        }

        private void GetReserveEdges(List<Vertex> _vertexList, List<Vertex> _reserveVertexList, List<Tuple<int, byte>>[,,,,] _locationArray, IProgress<int> _progress, CancellationToken _ct, int _progressWeight)
        {
            //Console.WriteLine("Debug GetEdges:");
            List<Tuple<int, byte>> vertexRefs;
            Vertex vertex;
            Sample sample;
            byte dimMax = (byte)(byte.MaxValue >> distancePrecision), maxDelta = (byte)(distanceMax >> distancePrecision);
            //Console.WriteLine($"Debug GetEdges: maxDelta {maxDelta} , dimMax {dimMax}");
            int bytesPerSample = CarrierMedia.BytesPerSample;
            Edge newEdge;
            short distance;
            int temp;
            int[] minValues = new int[bytesPerSample], maxValues = new int[bytesPerSample];
            byte sampleTargetValue;
            byte[] outerSampleValues, innerSampleValues, bestSwaps = new byte[2];


            for (int numVertex = 0; numVertex < _vertexList.Count; numVertex++)
            {
                _ct.ThrowIfCancellationRequested();
                vertex = _vertexList[numVertex];

                for (byte sampleIndex = 0; sampleIndex < samplesPerVertex; sampleIndex++)
                {
                    sample = vertex.Samples[sampleIndex];
                    outerSampleValues = sample.Values;
                    sampleTargetValue = sample.TargetModValue;
                    bestSwaps[0] = sampleIndex;

                    for (int byteIndex = 0; byteIndex < bytesPerSample; byteIndex++)
                    {
                        temp = (outerSampleValues[byteIndex] >> distancePrecision);
                        minValues[byteIndex] = ((temp - maxDelta) < 0) ? 0 : (temp - maxDelta);
                        maxValues[byteIndex] = ((temp + maxDelta) > dimMax) ? dimMax : (temp + maxDelta);
                    }

                    for (int x = minValues[0]; x <= maxValues[0]; x++)
                    {
                        for (int y = minValues[1]; y <= maxValues[1]; y++)
                        {
                            for (int z = minValues[2]; z <= maxValues[2]; z++)
                            {
                                vertexRefs = _locationArray[x, y, z, sampleTargetValue, 0];
                                if (vertexRefs != null)
                                {
                                    foreach (Tuple<int, byte> vertexRef in vertexRefs)
                                    {
                                        innerSampleValues = _reserveVertexList[vertexRef.Item1].Samples[vertexRef.Item2].Values;
                                        bestSwaps[1] = vertexRef.Item2;

                                        distance = 0;
                                        for (int valueIndex = 0; valueIndex < bytesPerSample; valueIndex++)
                                        {
                                            temp = outerSampleValues[valueIndex] - innerSampleValues[valueIndex];
                                            distance += (short)(temp * temp);
                                        }

                                        newEdge = new Edge(numVertex, vertexRef.Item1, distance, bestSwaps);

                                        vertex.Edges.Add(newEdge);
                                    }
                                }
                            }
                        }
                    }
                }

                //// Update progress counter.
                //if (progressCounter == progressUpdateInterval)
                //{
                //    progressCounter = 1;
                //    _progress?.Report(++progress);
                //    //Console.WriteLine($"... {numVertex} of {numVertices} handled. {(decimal)numVertex / numVertices:p}");
                //}
            }
            //Console.WriteLine("GetEdges: Successful.");
        }

        #endregion

        #region Extract
        public override StegoMessage Extract()
        {
            int numSamples = CarrierMedia.ByteArray.Length / CarrierMedia.BytesPerSample;
            modFactor = (byte)(1 << messageBitsPerVertex);
            bitwiseModFactor = (byte)(modFactor - 1);

            // Generate random numbers
            RandomNumberList randomNumbers = new RandomNumberList(Seed, numSamples);

            // Read bytes and verify GraphTheorySignature
            if (!ReadBytes(randomNumbers, Signature.Length).SequenceEqual(Signature))
            {
                throw new StegoAlgorithmException("Signature is invalid, possibly using a wrong key.");
            }

            // Read length
            int length = BitConverter.ToInt32(ReadBytes(randomNumbers, 4), 0);

            // Read data and return StegoMessage instance
            return new StegoMessage(ReadBytes(randomNumbers, length), CryptoProvider);
        }

        /// <summary>
        /// Converts the StegoMessage to a list of byte values according to the MessageBitsPerVertex property.
        /// </summary>
        private List<byte> GetMessageChunks(StegoMessage _message)
        {
            // Combine signature with message and convert to BitArray.
            BitArray messageBitArray = new BitArray(Signature.Concat(_message.ToByteArray(CryptoProvider)).ToArray());

            // Prepare list of bytes.
            int numMessageChunks = messageBitArray.Length / MessageBitsPerVertex;
            List<byte> messageChunklist = new List<byte>(numMessageChunks);

            // Insert each chunk.
            int indexOffset = 0;
            for (int i = 0; i < numMessageChunks; i++)
            {
                // Find current chunk value.
                byte messageValue = 0;
                for (int byteIndex = 0; byteIndex < messageBitsPerVertex; byteIndex++)
                {
                    messageValue += messageBitArray[indexOffset + byteIndex] ? (byte)(1 << byteIndex) : (byte)0;
                }

                // Increment offset.
                indexOffset += messageBitsPerVertex;

                // Add chunk to list.
                messageChunklist.Add(messageValue);
            }

            return messageChunklist;
        }
        
        /// <summary>
        /// Reads the specified number of bytes from the carrier media.
        /// </summary>
        private byte[] ReadBytes(RandomNumberList _numberList, int _count)
        {
            BitArray tempBitArray = new BitArray(_count * 8);
            int bytesPerSample = CarrierMedia.BytesPerSample;

            // Calculates the number of vertices that would be needed to represent the given number of bytes.
            int numVertices = (_count * 8) / messageBitsPerVertex;

            // Iterates through the number of vertices.
            for (int vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
            {
                int bitIndexOffset = vertexIndex * messageBitsPerVertex;
                int tempValue = 0;

                // Read all bytes necessary for a vertex.
                for (int sampleIndex = 0; sampleIndex < samplesPerVertex; sampleIndex++)
                {
                    // Calculates the location of the CarrierMedia.ByteArray to read.
                    var byteIndexOffset = _numberList.Next * bytesPerSample;
                    // Reads the bytes necessary for a sample from that location.
                    for (int byteIndex = 0; byteIndex < bytesPerSample; byteIndex++)
                    {
                        tempValue += CarrierMedia.ByteArray[byteIndexOffset + byteIndex];
                    }
                }

                // Calculates the representative value.
                tempValue = tempValue & bitwiseModFactor;

                // Converts the representative value to bits.
                for (int bitIndex = 0; bitIndex < messageBitsPerVertex; bitIndex++)
                {
                    tempBitArray[bitIndexOffset + bitIndex] = ((tempValue & (1 << bitIndex)) != 0);
                }
            }

            // Copy bitArray to new byteArray
            byte[] tempByteArray = new byte[_count];
            tempBitArray.CopyTo(tempByteArray, 0);

            return tempByteArray;
        }
        #endregion

    }
}