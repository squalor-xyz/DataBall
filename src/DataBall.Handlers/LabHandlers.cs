namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// Registers the custom-CSV primary handler and lab translator slots.
    /// Safe to call more than once; duplicates are the caller's problem — tests should
    /// <see cref="DataBall.ClearHandlers"/> first.
    /// </summary>
    public static class LabHandlers
    {
        public static void RegisterDefaults()
        {
            DataBall.RegisterHandler(new CustomCsvHandler());
            DataBall.RegisterHandler(new StdfHandler());
            DataBall.RegisterHandler(new TouchstoneHandler());
            DataBall.RegisterHandler(new ProductionHandler());
        }
    }
}
