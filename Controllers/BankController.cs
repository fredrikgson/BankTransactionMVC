using System;
using System.Collections.Generic;
using Laboration2.Models;
using System.Data.SQLite;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Laboration2.Controllers
{
    public class BankController : Controller
    {
        string authToken = APIKeys.authToken;
        string apiAddress = APIKeys.apiAddress;


        public IActionResult Index()
        {
            return View();
        }

        public IActionResult ListTransactions()
        {
            List<Transaction> fromDB = GetTransactionsFromDatabase();
            if (!fromDB.Any())
            {
                try
                {
                    AddTransactionsToDatabase(GetTransactionsFromAPI());
                }
                catch
                {
                    return View("Error", "The transaction data could not be fetched from the Stuxbank API. Please try again later.");
                }
            }
            List<Transaction> transactions = GetTransactionsFromDatabase();
            // apply reference rules
            transactions = ApplyReferenceRules(transactions);

            // Apply transaction rules
            transactions = ApplyTransactionRules(transactions);

            // also, fetch all categories from database
            List<string> categories = GetCategoriesFromDatabase();

            (List<Transaction>, List<string>) data = (transactions, categories);

            return View(data);
        }

        public IActionResult ReloadTransactions()
        {
            ClearTransactionsFromDatabase();
            return RedirectToAction("ListTransactions");
        }

        public IActionResult ManageCategories()
        {
            List<string> categories = GetCategoriesFromDatabase();
            return View(categories);
        }

        public IActionResult ManageReferenceRules()
        {
            // Manage kategorier
            List<string> allReferences = GetDistinctReferencesFromDatabase();
            List<string> allCategories = GetCategoriesFromDatabase();
            Dictionary<string, string> referenceRules = GetReferenceRulesFromDatabase();

            (List<string>, List<string>, Dictionary<string, string>) data = (allReferences,allCategories,referenceRules);

            return View(data);
        }

        public IActionResult SummationReport()
        {
            float totalRevenue = GetTotalRevenueFromDatabase();
            float totalExpenditure = GetTotalExpenditureFromDatabase();

            List<Transaction> transactions = GetTransactionsFromDatabase();
            transactions = ApplyReferenceRules(transactions);
            transactions = ApplyTransactionRules(transactions);
            List<string> categories = GetCategoriesFromDatabase();

            var revByCat = transactions.Where(t => t.Amount > 0)
                .GroupBy(t => t.Category)
                .Select(g => new { Category = g.Key, TotalAmount = g.Sum(t => t.Amount) });
            var expByCat = transactions.Where(t => t.Amount < 0)
                .GroupBy(t => t.Category)
                .Select(g => new { Category = g.Key, TotalAmount = g.Sum(t => t.Amount) });

            // convert these IEnumerable<*AnonymousObject*> to a format that we
            // can pass along to the view
            Dictionary<string, float> revenueByCategory = new Dictionary<string, float>();
            foreach(var item in revByCat)
            {
                revenueByCategory.Add(item.Category, item.TotalAmount);
            }
            Dictionary<string, float> expenditureByCategory = new Dictionary<string, float>();
            foreach (var item in expByCat)
            {
                expenditureByCategory.Add(item.Category, item.TotalAmount);
            }

            // before sending off to View, compile and download the data
            CompileAndDownloadXML(totalRevenue, totalExpenditure, revenueByCategory, expenditureByCategory);

            (float, float, Dictionary<string, float>, Dictionary<string, float>) data
                = (totalRevenue, totalExpenditure, revenueByCategory, expenditureByCategory);

            return View(data);
        }

        public IActionResult Error(string errorMessage)
        {
            return View(errorMessage);
        }

        // OBSOLETE
        [Obsolete]
        public IActionResult ManageTransactionRule()
        {
            // kategorisering för en enda transaktion
            return View();
        }

        [HttpPost]
        public IActionResult AddCategory()
        {
            string newCategory = Request.Form["newCategory"];
            if (ValidateNewCategory(newCategory))
            {
                AddCategoryToDatabase(newCategory);
            }
            else
            {
                return View("Error", "The category name you entered was invalid. Please enter a valid category name that does not already exist.");
            }
            return RedirectToAction("ManageCategories");
        }

        [HttpPost]
        public IActionResult ApplyCategory()
        {
            string newCategory = FormatCategoryName(Request.Form["category"]);
            string transactionID = Request.Form["transactionID"];

            // kolla om kategorin redan finns
            // on inte, lägg till den
            List<string> _cats = GetCategoriesFromDatabase();
            if (!_cats.Contains(newCategory))
            {
                AddCategoryToDatabase(newCategory);
            }

            AddTransactionRuleToDatabase(transactionID, newCategory);

            return RedirectToAction("ListTransactions");
        }

        [HttpPost]
        public IActionResult AddReferenceRule()
        {
            string reference = Request.Form["reference"];
            string category = Request.Form["category"];

            // add rule to database
            AddReferenceRuleToDatabase(reference, category);

            return RedirectToAction("ManageReferenceRules");
        }



        public List<Transaction> GetTransactionsFromAPI()
        {
            string jsonResult = string.Empty;
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", authToken);
            using (HttpResponseMessage response = client.GetAsync(apiAddress).Result)
            {
                using(HttpContent content = response.Content)
                {
                    jsonResult = content.ReadAsStringAsync().Result;
                }
            }
            return JsonSerializer.Deserialize<List<Transaction>>(jsonResult);
        }

        public List<Transaction> GetTransactionsFromDatabase()
        {
            List<Transaction> transactions = new List<Transaction>();
            using(var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = "SELECT * FROM Transactions";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    Transaction transaction = new Transaction();
                    transaction.Initialize(reader.GetInt32(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3), reader.GetFloat(4),
                        reader.GetFloat(5), reader.GetString(6));
                    transactions.Add(transaction);
                }

                connection.Close();
            }
            return transactions;
        }

        public void AddTransactionsToDatabase(List<Transaction> transactions)
        {
            using(var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();
                using(var sqlTransaction = connection.BeginTransaction())
                {
                    using var command = new SQLiteCommand(@"INSERT INTO Transactions
                            (TransactionID, BookingDate, TransactionDate, Reference, Amount, Balance, Category)
                            VALUES(@TransactionID, @BookingDate, @TransactionDate, @Reference, @Amount, @Balance, @Category)",
                            connection);
                    var transactionID = command.Parameters.AddWithValue("@TransactionID", 0);
                    var bookingDate = command.Parameters.AddWithValue("@BookingDate", "");
                    var transactionDate = command.Parameters.AddWithValue("@TransactionDate", "");
                    var reference = command.Parameters.AddWithValue("@Reference", "");
                    var amount = command.Parameters.AddWithValue("@Amount", 0.0);
                    var balance = command.Parameters.AddWithValue("@Balance", 0.0);
                    var category = command.Parameters.AddWithValue("@Category", "");
                    foreach(Transaction transaction in transactions)
                    {
                        transactionID.Value = transaction.TransactionID;
                        bookingDate.Value = transaction.BookingDate;
                        transactionDate.Value = transaction.TransactionDate;
                        reference.Value = transaction.Reference;
                        amount.Value = transaction.Amount;
                        balance.Value = transaction.Balance;
                        category.Value = transaction.Category;   // kanske lägga i en konstant i Transaction

                        command.ExecuteNonQuery();
                    }
                    sqlTransaction.Commit();
                }
                connection.Close();
            }
        }

        public void ClearTransactionsFromDatabase()
        {
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();
                using (var sqlTransaction = connection.BeginTransaction())
                {
                    using var command = new SQLiteCommand("DELETE FROM Transactions",
                            connection);
                    command.ExecuteNonQuery();
                    sqlTransaction.Commit();
                }
                connection.Close();
            }
        }

        public List<string> GetCategoriesFromDatabase()
        {
            List<string> categories = new List<string>();
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = "SELECT Category FROM Categories";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string category = reader.GetString(0);
                    categories.Add(category);
                }

                connection.Close();
            }
            return categories;
        }

        public void AddCategoryToDatabase(string newCategory)
        {
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();
                using (var sqlTransaction = connection.BeginTransaction())
                {
                    using var command = new SQLiteCommand(@"INSERT INTO Categories (Category) VALUES (@Category)",
                            connection);
                    var category = command.Parameters.AddWithValue("@Category", "");
                    category.Value = newCategory;
                    command.ExecuteNonQuery();
                    sqlTransaction.Commit();
                }
                connection.Close();
            }
        }

        public void AddTransactionRuleToDatabase(string transactionID, string category)
        {
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();
                using (var sqlTransaction = connection.BeginTransaction())
                {
                    // kolla om rule för givet transactionID redan finns
                    Dictionary<string, string> existingRules = GetTransactionRulesFromDatabase();
                    SQLiteCommand command;
                    if (existingRules.ContainsKey(transactionID))
                    {
                        command = new SQLiteCommand(@"UPDATE TransactionRules
                                                    SET Category = @Category
                                                    WHERE TransactionID = @TransactionID", connection);
                    }
                    else
                    {

                        command = new SQLiteCommand(@"INSERT INTO TransactionRules (TransactionID, Category) VALUES(@TransactionID, @Category)",
                                connection);
                    }
                    var _transactionID = command.Parameters.AddWithValue("@TransactionID", "");
                    var _category = command.Parameters.AddWithValue("@Category", "");
                    _transactionID.Value = transactionID;
                    _category.Value = category;
                    command.ExecuteNonQuery();
                    sqlTransaction.Commit();
                }
                connection.Close();
            }
        }

        public void AddReferenceRuleToDatabase(string reference, string category)
        {
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();
                using (var sqlTransaction = connection.BeginTransaction())
                {
                    // kolla om rule för givet transactionID redan finns
                    Dictionary<string, string> existingRules = GetReferenceRulesFromDatabase();
                    SQLiteCommand command;
                    if (existingRules.ContainsKey(reference))
                    {
                        command = new SQLiteCommand(@"UPDATE ReferenceRules
                                                    SET Category = @Category
                                                    WHERE Reference = @Reference", connection);
                    }
                    else
                    {
                        command = new SQLiteCommand(@"INSERT INTO ReferenceRules (Reference, Category) VALUES(@Reference, @Category)",
                                connection);
                    }
                    var _reference = command.Parameters.AddWithValue("@Reference", "");
                    var _category = command.Parameters.AddWithValue("@Category", "");
                    _reference.Value = reference;
                    _category.Value = category;
                    command.ExecuteNonQuery();
                    sqlTransaction.Commit();
                }
                connection.Close();
            }
        }

        public List<string> GetDistinctReferencesFromDatabase()
        {
            List<string> references = new List<string>();
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = "SELECT DISTINCT Reference FROM Transactions";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string reference = reader.GetString(0);
                    references.Add(reference);
                }

                connection.Close();
            }
            return references;
        }

        public Dictionary<string,string> GetTransactionRulesFromDatabase()
        {
            Dictionary<string, string> transactionRules = new Dictionary<string, string>();
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = "SELECT * FROM TransactionRules";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string _transactionID = reader.GetInt32(0).ToString();
                    string _category = reader.GetString(1);
                    transactionRules.Add(_transactionID, _category);
                }
                connection.Close();
            }
            return transactionRules;
        }

        public Dictionary<string, string> GetReferenceRulesFromDatabase()
        {
            Dictionary<string, string> referenceRules = new Dictionary<string, string>();
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = "SELECT * FROM ReferenceRules";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string _reference = reader.GetString(0);
                    string _category = reader.GetString(1);
                    referenceRules.Add(_reference, _category);
                }
                connection.Close();
            }
            return referenceRules;
        }


        public float GetTotalRevenueFromDatabase()
        {
            float totalRevenue = 0.0f; // if total revenue could not be fetched from DB
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = @"SELECT SUM(Amount) AS SumRevenue
                                FROM Transactions
                                WHERE Amount > 0";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    float result = reader.GetFloat(0);
                    totalRevenue = result;
                }
                connection.Close();
            }
            return totalRevenue;
        }

        public float GetTotalExpenditureFromDatabase()
        {
            float totalExpenditure = 0.0f; // if total revenue could not be fetched from DB
            using (var connection = new SQLiteConnection("Data Source=transactions.db"))
            {
                connection.Open();

                string query = @"SELECT SUM(Amount) AS SumRevenue
                                FROM Transactions
                                WHERE Amount < 0";
                using var cmd = new SQLiteCommand(query, connection);
                using SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    totalExpenditure = reader.GetFloat(0);
                }
                connection.Close();
            }
            return totalExpenditure;
        }


        public List<Transaction> ApplyTransactionRules(List<Transaction> transactions)
        {
            Dictionary<string, string> transactionRules = GetTransactionRulesFromDatabase();

            foreach(var rule in transactionRules)
            {
                foreach(Transaction transaction in transactions)
                {
                    if(transaction.TransactionID.ToString() == rule.Key)
                    {
                        transaction.Category = rule.Value;
                    }
                }
            }
            // applicera dem på transactions i listan
            return transactions;
        }

        public List<Transaction> ApplyReferenceRules(List<Transaction> transactions)
        {
            // contains transaction rules as Reference(key), Category(value)
            Dictionary<string, string> referenceRules = GetReferenceRulesFromDatabase();
            foreach (var rule in referenceRules)
            {
                foreach (Transaction transaction in transactions)
                {
                    if (transaction.Reference == rule.Key)
                    {
                        transaction.Category = rule.Value;
                    }
                }
            }
            return transactions;
        }

        public void CompileAndDownloadXML(
            float totalRevenue,
            float totalExpenditure,
            Dictionary<string, float> revenueByCategory,
            Dictionary<string, float> expenditureByCategory)
        {
            // totals
            XElement bankReport = new XElement("BankReport");
            bankReport.Add(new XElement("TotalRevenue", totalRevenue));
            bankReport.Add(new XElement("TotalExpenditure", totalExpenditure));

            // by category
            XElement xRevenueByCategory = new XElement("RevenueByCategory");
            foreach(var item in revenueByCategory)
            {
                //xRevenueByCategory.Add(item.Key, item.Value);
                XElement category = new XElement("Category");
                category.Add(new XElement("CategoryName", item.Key));
                category.Add(new XElement("Amount", item.Value));
                xRevenueByCategory.Add(category);
            }
            XElement xExpenditureByCategory = new XElement("ExpenditureByCategory");
            foreach (var item in expenditureByCategory)
            {
                //xExpenditureByCategory.Add(new XElement(item.Key, item.Value));
                XElement category = new XElement("Category");
                category.Add(new XElement("CategoryName", item.Key));
                category.Add(new XElement("Amount", item.Value));
                xExpenditureByCategory.Add(category);
            }

            // compile
            bankReport.Add(xRevenueByCategory, xExpenditureByCategory);

            // save
            bankReport.Save("BankReport.xml");
        }

        // formatting
        public string FormatCategoryName(string categoryName)
        {
            categoryName = categoryName.Trim();
            categoryName = char.ToUpper(categoryName[0]) + categoryName.Substring(1);
            return categoryName;
        }

        // security and validation and error handling
        public bool ValidateNewCategory(string newCategory)
        {
            if(newCategory != null
                && newCategory != ""
                && newCategory.Trim() != null
                && newCategory.Trim() != ""
                && !GetCategoriesFromDatabase().Contains(newCategory))
            {
                return true;
            }
            return false;
        }
    }
}