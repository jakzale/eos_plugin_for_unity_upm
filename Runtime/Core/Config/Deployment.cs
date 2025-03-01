/*
 * Copyright (c) 2024 PlayEveryWare
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

namespace PlayEveryWare.EpicOnlineServices
{
    using Newtonsoft.Json;
    using Utility;
    using System;

    public struct Deployment : IEquatable<Deployment>
    {
        /// <summary>
        /// The SandboxId for the deployment.
        /// </summary>
        public SandboxId SandboxId;

        /// <summary>
        /// The DeploymentId.
        /// </summary>
        public Guid DeploymentId;

        /// <summary>
        /// Indicates whether a deployment is completely defined or not. A
        /// deployment is completely defined if neither the sandbox id nor the
        /// deployment id are empty.
        /// </summary>
        [JsonIgnore]
        public readonly bool IsComplete
        {
            get
            {
                return !DeploymentId.Equals(Guid.Empty) && !SandboxId.IsEmpty;
            }
        }

        public bool Equals(Deployment other)
        {
            return SandboxId.Equals(other.SandboxId) && DeploymentId.Equals(other.DeploymentId);
        }

        public override bool Equals(object obj)
        {
            return obj is Deployment deployment && Equals(deployment);
        }

        public override int GetHashCode()
        {
            return HashUtility.Combine(SandboxId, DeploymentId);
        }

        public override string ToString()
        {
            return $"DeploymentId: {DeploymentId.ToString("N").ToLower()}, SandboxId: {SandboxId}";
        }
    }

}
