// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// Registers the custom-CSV primary handler and lab translator slots.
    /// Safe to call more than once; handler types already registered are skipped.
    /// </summary>
    public static class LabHandlers
    {
        public static void RegisterDefaults()
        {
            DataBall.RegisterHandlerIfMissing(new CustomCsvHandler());
            DataBall.RegisterHandlerIfMissing(new StdfHandler());
            DataBall.RegisterHandlerIfMissing(new TouchstoneHandler());
            DataBall.RegisterHandlerIfMissing(new ProductionHandler());
        }
    }
}
