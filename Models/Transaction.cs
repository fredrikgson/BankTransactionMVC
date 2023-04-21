using System;
namespace Laboration2.Models
{
    public class Transaction
    {
        public int TransactionID { get; set; }
        public string BookingDate { get; set; }
        public string TransactionDate { get; set; }
        public string Reference { get; set; }
        public float Amount { get; set; }
        public float Balance { get; set; }

        public string Category { get; set; } = "Övrigt";

        public void Initialize(int TransactionID, string BookingDate,
            string TransactionDate, string Reference, float Amount, float Balance,
            string? Category)
        {
            this.TransactionID = TransactionID;
            this.BookingDate = BookingDate;
            this.TransactionDate = TransactionDate;
            this.Reference = Reference;
            this.Amount = Amount;
            this.Balance = Balance;
            this.Category = Category;
        }
    }
}
