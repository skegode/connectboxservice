namespace ConnectBoxService.Models
{
    /// <summary>Matches the /auth/token response</summary>
    public class TokenResponse
    {
        public bool          Success { get; set; }
        public string        Message { get; set; } = "";
        public TokenData?    Data    { get; set; }
    }

    public class TokenData
    {
        public string AccessToken { get; set; } = "";
        public string TokenType   { get; set; } = "";
        public int    ExpiresIn   { get; set; }
    }

    /// <summary>Matches the /loans response</summary>
    public class LoanApiResponse
    {
        public bool          Success { get; set; }
        public string        Message { get; set; } = "";
        public LoanDataList? Data    { get; set; }
    }

    public class LoanDataList
    {
        public int            Count { get; set; }
        public List<LoanDto>  Data  { get; set; } = new();
    }

    public class LoanDto
    {
        public string   Id               { get; set; } = "";
        public string   FirstName        { get; set; } = "";
        public string   OtherName        { get; set; } = "";
        public string   PhoneNumber      { get; set; } = "";
        public string   EmailAddress     { get; set; } = "";
        public string   NationalId       { get; set; } = "";
        public decimal  AmountToDisburse { get; set; }
        public string   RepaymentPeriod  { get; set; } = "";
        public decimal  Arrears          { get; set; }
        public int      DaysInArrears    { get; set; }
        public decimal  LoanBalance      { get; set; }
        public string   Branch           { get; set; } = "";
        public decimal  OutsourcedAmount { get; set; }
        public decimal  Penalty          { get; set; }
        public string   ProductName      { get; set; } = "";
        public string   Agent            { get; set; } = "";
        public string   AgentId          { get; set; } = "";
        public string borrowerId { get; set; } = "";
    }
}
