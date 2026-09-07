using System.ComponentModel.DataAnnotations.Schema;

namespace ConnectBoxAPI.Models
{
    public class Contract
    {
        public int Id { get; set; }

        public string ContractName { get; set; } = string.Empty;

        public int CompanyId { get; set; }

        public int? AgentAttached { get; set; }

        public string? FileAttached { get; set; }

        public string? FilePath { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime? CallDate { get; set; }

        public string? ContractType { get; set; }

        public int? CategoryId { get; set; }

        public int? CreatedBy { get; set; }

        public DateTime CreatedDate { get; set; }

        public int? UpdatedBy { get; set; }

        public DateTime? UpdatedDate { get; set; }
    }
}