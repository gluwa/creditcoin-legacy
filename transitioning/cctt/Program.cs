using System;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;

namespace cctt
{
    class Program
    {
        private const string DATA = "data";
        private const string TRANSACTIONS = "transactions";
        private const string HEADER_SIGNATURE = "header_signature";
        private const string ERROR = "error";
        private const string MESSAGE = "message";
        private const string HEADER = "header";
        private const string BATCHES = "batches";
        private const string PAYLOAD = "payload";
        private const string BLOCK_NUM = "block_num";
        private const string PAGING = "paging";
        private const string NEXT = "next";
        private const string CONSENSUS = "consensus";
        private const string PREVIOUS_BLOCK_ID = "previous_block_id";
        private const string SIGNER_PUBLIC_KEY = "signer_public_key";
        private const string FAMILY_NAME = "family_name";
        private const string FAMILY_VERSION = "family_version";
        private const string ADDRESS = "address";
        private const string TRANSACTION_IDS = "transaction_ids";
        private const string BATCHER_PUBLIC_KEY = "batcher_public_key";
        private const string INPUTS = "inputs";
        private const string NONCE = "nonce";
        private const string OUTPUTS = "outputs";
        private const string PAYLOAD_SHA512 = "payload_sha512";
        private const string BATCH_IDS = "batch_ids";
        private const string STATE_ROOT_HASH = "state_root_hash";

        private const int IDX_POW = 0;
        private const int IDX_DIFFICULTY = 1;
        private const int IDX_NONCE = 2;
        private const int IDX_TIME = 3;

        private const int SKIP_TO_GET_60 = 512 / 8 * 2 - 60; // 512 - hash size, 8 - bits in byte, 2 - hex digits for byte, 60 - merkle address length (70) without namespace length (6) and prexix length (4)

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: cctt connection filename");
                return;
            }

            string connection = args[0]; //http://rest-api:8008
            string filename = args[1];
            string url = $"{connection}/blocks";

            if (System.IO.File.Exists(filename))
            {
                Console.WriteLine($"file exists: {filename}");
                return;
            }

            try
            {
                HttpClient httpClient = new HttpClient();

                using (System.IO.StreamWriter streamWriter = new System.IO.StreamWriter(filename))
                {
                    for (; ; )
                    {
                        var httpResponse = httpClient.GetAsync(url).Result;
                        string responseJson = httpResponse.Content.ReadAsStringAsync().Result;

                        var response = JObject.Parse(responseJson);
                        if (response.ContainsKey(ERROR))
                        {
                            Console.WriteLine((string)response[ERROR][MESSAGE]);
                            return;
                        }
                        if (!response.ContainsKey(DATA))
                        {
                            Console.WriteLine($"Expecting data in {responseJson}");
                            return;
                        }
                        var data = (JArray)response[DATA];
                        foreach (var datum in data)
                        {
                            var blockObj = (JObject)datum;

                            if (!blockObj.ContainsKey(HEADER))
                            {
                                Console.WriteLine($"Expecting a header in {responseJson}");
                                return;
                            }
                            var blockHeader = (JObject)blockObj[HEADER];
                            if (!blockHeader.ContainsKey(BLOCK_NUM))
                            {
                                Console.WriteLine($"Expecting a block_num in {responseJson}");
                                return;
                            }
                            var blockNum = (string)blockHeader[BLOCK_NUM];
                            streamWriter.WriteLine(blockNum);
                            var blockSignerPublicKey = (string)blockHeader[SIGNER_PUBLIC_KEY];
                            streamWriter.WriteLine(blockSignerPublicKey);

                            var batchArray = (JArray)blockObj[BATCHES];
                            foreach (var batchElement in batchArray)
                            {
                                var batchObj = (JObject)batchElement;
                                var transactionArray = (JArray)batchObj[TRANSACTIONS];
                                foreach (var transactionElement in transactionArray)
                                {
                                    var transactionObj = (JObject)transactionElement;
                                    var transactionHeader = (JObject)transactionObj[HEADER];
                                    var nonce = (string)transactionHeader[NONCE];
                                    streamWriter.WriteLine(nonce);
                                    var sighash = sighashFromPubKey((string)transactionHeader[SIGNER_PUBLIC_KEY]);
                                    streamWriter.WriteLine(sighash);
                                    var payload = (string)transactionObj[PAYLOAD];
                                    streamWriter.WriteLine(payload);
                                }
                            }
                            streamWriter.WriteLine(".");
                        }

                        if (!response.ContainsKey(PAGING))
                        {
                            Console.WriteLine($"Expecting a paging in {responseJson}");
                            return;
                        }

                        var paging = (JObject)response[PAGING];
                        if (!paging.ContainsKey(NEXT))
                            break;
                        var next = (string)paging[NEXT];
                        url = next;
                    }
                }
                Console.WriteLine("Done!");
            }
            catch (Exception x)
            {
                Console.WriteLine("Unexpected: " + x.Message);
            }
        }

        private static string sighashFromPubKey(string pubKey)
        {
            if (!pubKey.All("1234567890abcdef".Contains))
                return null;
            var compressionFlag = pubKey.Substring(0, 2 * 1);
            if (pubKey.Length == 2 * 1 + 2 * 32 + 2 * 32 && compressionFlag.Equals("04"))
            {
                // this is an uncompressed pub key, compress
                var yLast = pubKey.Substring(2 * 1 + 2 * 32 + 2 * 31, 2 * 1);
                int value = int.Parse(yLast, System.Globalization.NumberStyles.HexNumber);
                pubKey = ((value % 2 == 0) ? "02" : "03") + pubKey.Substring(2 * 1, 2 * 32);
            }
            else if (pubKey.Length != 2 * 1 + 2 * 32 || !compressionFlag.Equals("02") && !compressionFlag.Equals("03"))
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(pubKey);
            string sighash;
            using (SHA512 sha512 = new SHA512Managed())
            {
                var hash = sha512.ComputeHash(bytes);
                var hashString = string.Concat(Array.ConvertAll(hash, x => x.ToString("X2")));
                sighash = hashString.Substring(SKIP_TO_GET_60).ToLower();
            }
            return sighash;
        }
    }
}
