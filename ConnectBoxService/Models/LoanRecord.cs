namespace ConnectBoxService.Models
{
    /// <summary>
    /// Represents a loan record from the loans API,
    /// mapped to ContractData table columns.
    /// </summary>
    public class LoanRecord
    {
        public int      ContractId       { get; set; }
        public string   LoanId           { get; set; } = "";
        public string   FirstName        { get; set; } = "";
        public string   OtherNames       { get; set; } = "";
        public string   PhoneNumber      { get; set; } = "";
        public string   EmailAddress     { get; set; } = "";
        public string   IdOrPassport     { get; set; } = "";
        public decimal  AmountDisbursed  { get; set; }
        public string   Installments     { get; set; } = "";
        public decimal  ArrearsAmount    { get; set; }
        public int      DaysInArrears    { get; set; }
        public decimal  Olb              { get; set; }
        public string   Branch           { get; set; } = "";
        public decimal  OutSourcedAmount { get; set; }
        public string   EntityId         { get; set; } = "";
        public decimal  Penalty          { get; set; }
    }
}
