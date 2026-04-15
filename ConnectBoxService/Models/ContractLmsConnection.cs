namespace ConnectBoxService.Models
{
    /// <summary>
    /// Represents a row from the ContractLmsConnections query —
    /// includes joined data from Contracts, Companies, RefreshCycles, and custom filtering ranges.
    /// </summary>
    public class ContractLmsConnection
    {
        // ContractLmsConnections main columns
        public int Id { get; set; }
        public int ContractId { get; set; }
        public int DataRefreshCycle { get; set; }
        public int PaymentsRefreshCycle { get; set; }

        // --- FILTERING CRITERIA (Added) ---
        // Nullable types ensure we only filter when values are actually set in the DB
        public int? MinDays { get; set; }
        public int? MaxDays { get; set; }
        public decimal? MinAmount { get; set; }
        public decimal? MaxAmount { get; set; }
        public decimal? MinOlb { get; set; }
        public decimal? MaxOlb { get; set; }
        public DateTime? LastDataFetch { get; set; }
        public DateTime? NextDataFetch { get; set; }
        public DateTime? LastPaymentsFetch { get; set; }
        public DateTime? NextPaymentsFetch { get; set; }
        public string CategoryId { get; set; }

        // From Companies (via Contracts)
        public string EntityId { get; set; } = "";
        public string LmsEntityId { get; set; } = "";

        // From RefreshCycles (data)
        public string DataRefreshCycleName { get; set; } = "";
        public int DataRefreshCycleMinutes { get; set; }

        // From RefreshCycles (payments)
        public string PaymentsRefreshCycleName { get; set; } = "";
        public int PaymentsRefreshCycleMinutes { get; set; }
    }
}