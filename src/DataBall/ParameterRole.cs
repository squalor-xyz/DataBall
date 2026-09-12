// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall
{
    /// <summary>
    /// Schema role of a canonical parameter after header parse.
    /// </summary>
    public static class ParameterRole
    {
        public const string Identity = "identity";
        public const string Stimulus = "stimulus";
        public const string Meas = "meas";
        public const string Classification = "classification";
        public const string Metadata = "metadata";
    }
}
