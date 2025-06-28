using System;

namespace TCServer.Common.Models
{
    /// <summary>
    /// 账户信息模型
    /// </summary>
    public class AccountInfo
    {
        public int AcctId { get; set; }
        public string? AcctName { get; set; }
        public DateTime? AcctDate { get; set; }
        public string? Memo { get; set; }
        public string? ApiKey { get; set; }
        public string? SecretKey { get; set; }
        public string? ApiPass { get; set; }
        public int? State { get; set; }
        public int? Status { get; set; }
        public string? Email { get; set; }
        public int? GroupId { get; set; }
        public int? SendFlag { get; set; }
    }
} 