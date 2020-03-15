using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ccbe
{
    internal static class Cache
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

        public static string creditcoinUrl;

        private static List<Models.Block> blocks = new List<Models.Block>();
        private static Dictionary<string, int?> id2idx = new Dictionary<string, int?>();
        private static Dictionary<int, string> idx2id = new Dictionary<int, string>();
        private static Dictionary<string, string> wallets = new Dictionary<string, string>();
        private static HttpClient httpClient = new HttpClient();

        private class Success
        {
            public bool value = false;
        }

        private static Success success = new Success();

        public static bool IsSuccessful()
        {
            lock (success)
                return success.value;
        }

        public static Models.Block GetBlock(string id)
        {
            Models.Block ret;
            lock (blocks)
            {
                int? index;
                id2idx.TryGetValue(id, out index);
                if (index == null)
                    return null;
                ret = blocks[index.Value];
                Debug.Assert(ret != null);
            }
            return ret;
        }
        public static Blocks GetBlocks(string id, int count)
        {
            var cont = new List<KeyValuePair<string, Models.Block>>();
            var ret = new Blocks(cont);
            lock (blocks)
            {
                int last;
                if (id != null)
                {
                    int? index;
                    id2idx.TryGetValue(id, out index);
                    if (index == null)
                        return null;
                    if (index.Value == 0)
                        return ret;
                    last = index.Value - 1;
                }
                else
                {
                    if (blocks.Count > 0)
                        last = blocks.Count - 1;
                    else
                        return ret;
                }
                int first;
                if (last < count)
                    first = 0;
                else
                    first = last - count + 1;

                for (int i = last; i >= first; --i)
                    cont.Add(new KeyValuePair<string, Models.Block>(idx2id[i], blocks[i]));
            }
            return ret;
        }

        public static Dictionary<string, Models.Block> findBlockWithTx(string txid)
        {
            lock (blocks)
            {
                for (int i = 0; i < blocks.Count; ++i)
                {
                    var block = blocks[i];
                    var elements = block.Transactions;
                    foreach (var element in elements)
                    {
                        if (element.Key.Equals(txid))
                        {
                            var ret = new Dictionary<string, Models.Block>();
                            ret.Add(idx2id[i], block);
                            return ret;
                        }
                    }
                }
            }
            return null;
        }

        public static Blocks findBlocksForSighash(string sighash, string id, int count)
        {
            Debug.Assert(count > 0);
            var cont = new List<KeyValuePair<string, Models.Block>>();
            lock (blocks)
            {
                int last;
                if (id != null)
                {
                    int? index;
                    id2idx.TryGetValue(id, out index);
                    if (index == null)
                        return null;
                    last = index.Value - 1;
                }
                else
                {
                    last = blocks.Count - 1;
                }
                for (int i = last; i >= 0; --i)
                {
                    var block = blocks[i];
                    var elements = block.Transactions;
                    foreach (var element in elements)
                    {
                        if (element.Value.Sighash.Equals(sighash))
                        {
                            cont.Add(new KeyValuePair<string, Models.Block>(idx2id[i], block));
                            if (cont.Count == count)
                                return new Blocks(cont);
                        }
                    }
                }
            }
            return new Blocks(cont);
        }

        public static Models.Block Tip()
        {
            Models.Block ret;
            lock (blocks)
            {
                ret = blocks[blocks.Count - 1];
            }
            return ret;
        }

        public static string GetWallet(string id)
        {
            string ret;
            lock (wallets)
            {
                wallets.TryGetValue(id, out ret);
            }
            return ret;
        }

        public static string calculateSupply()
        {
            var ret = new BigInteger();
            lock (wallets)
            {
                foreach (var entry in wallets)
                {
                    var value = BigInteger.Parse(entry.Value);
                    ret += value;
                }
            }
            return ret.ToString();
        }

        public static string UpdateWallets()
        {
            string url = $"{creditcoinUrl}/state?address=8a1a040000";

            var newWallets = new Dictionary<string, string>();

            try
            {
                for (; ; )
                {
                    using (HttpResponseMessage responseMessage = httpClient.GetAsync(url).Result)
                    {
                        var json = responseMessage.Content.ReadAsStringAsync().Result;
                        var response = JObject.Parse(json);
                        if (response.ContainsKey(ERROR))
                        {
                            var error = (JObject)response[ERROR];
                            if (!error.ContainsKey(MESSAGE))
                                return $"Expecting message in error in {response}";
                            return (string)error[MESSAGE];
                        }
                        else
                        {
                            if (!response.ContainsKey(DATA))
                                return $"Expecting data in {response}";
                            var data = response[DATA];
                            foreach (var datum in data)
                            {
                                var obj = (JObject)datum;
                                if (!obj.ContainsKey(ADDRESS))
                                    return $"Expecting address in {response}";
                                var objid = ((string)obj[ADDRESS]).Substring(10);
                                if (newWallets.ContainsKey(objid))
                                    return $"Duplicate wallet id in {response}";
                                if (!obj.ContainsKey(DATA))
                                    return $"Expecting data in {response}";
                                var content = (string)obj[DATA];
                                byte[] protobuf = Convert.FromBase64String(content);
                                Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                                BigInteger unused;
                                if (!BigInteger.TryParse(wallet.Amount, out unused))
                                    return $"Invalid numeric '{wallet.Amount}' for '{objid}' in {response}";

                                newWallets.Add(objid, wallet.Amount);
                            }

                            if (!response.ContainsKey(PAGING))
                                return $"Expecting paging in {response}";
                            var paging = (JObject)response[PAGING];
                            if (!paging.ContainsKey(NEXT))
                                break;
                            url = (string)paging[NEXT];
                        }
                    }
                }
            }
            catch (Exception x)
            {
                lock (success)
                    success.value = false;
                return $"Unexpected exception in UpdateWallets(): {x.Message}";
            }

            lock (wallets)
            {
                wallets = newWallets;
                lock (success)
                    success.value = true;
            }

            return null;
        }

        public static string UpdateCache()
        {
            string url = $"{creditcoinUrl}/blocks";
            string blockNum = "<no previous blocks processed>";

            var newBlocks = new Dictionary<int, Models.Block>();
            var newIds = new Dictionary<int, string>();

            try
            {
                bool done = false;
                for (; ; )
                {
                    var responseMessage = httpClient.GetAsync(url).Result;
                    var responseJson = responseMessage.Content.ReadAsStringAsync().Result;

                    var response = JObject.Parse(responseJson);
                    if (response.ContainsKey(ERROR))
                        return (string)response[ERROR][MESSAGE];
                    if (!response.ContainsKey(DATA))
                        return $"Expecting data in {responseJson}";

                    var data = (JArray)response[DATA];
                    foreach (var datum in data)
                    {
                        var blockObj = (JObject)datum;
                        int size = 0;

                        if (!blockObj.ContainsKey(HEADER))
                            return $"Expecting block header (after block {blockNum}) in {responseJson}";
                        var blockHeader = (JObject)blockObj[HEADER];
                        if (!blockHeader.ContainsKey(BLOCK_NUM))
                            return $"Expecting block_num (after block {blockNum}) in {responseJson}";
                        blockNum = (string)blockHeader[BLOCK_NUM];
                        int blockIndex;
                        if (!int.TryParse(blockNum, out blockIndex))
                            return $"Invalid numeric block_num '{blockNum}' (after block {blockNum}) in {responseJson}";

                        if (blocks.Count > blockIndex)
                        {
                            done = true;
                            break;
                        }

                        if (!blockObj.ContainsKey(HEADER_SIGNATURE))
                            return $"Expecting block header_signature (after block {blockNum}) in {responseJson}";
                        var blockId = (string)blockObj[HEADER_SIGNATURE];

                        string version = "undifined";
                        if (!blockObj.ContainsKey(BATCHES))
                            return $"Expecting block batches (after block {blockNum}) in {responseJson}";
                        var batchArray = (JArray)blockObj[BATCHES];
                        var transactions = new Dictionary<string, Models.Transaction>();
                        foreach (var batchElement in batchArray)
                        {
                            var batchObj = (JObject)batchElement;
                            if (!batchObj.ContainsKey(TRANSACTIONS))
                                return $"Expecting block transactions (after block {blockNum}) in {responseJson}";
                            var transactionArray = (JArray)batchObj[TRANSACTIONS];
                            foreach (var transactionElement in transactionArray)
                            {
                                var transactionObj = (JObject)transactionElement;
                                if (!transactionObj.ContainsKey(PAYLOAD))
                                    return $"Expecting transaction payload (after block {blockNum}) in {responseJson}";
                                if (!transactionObj.ContainsKey(HEADER_SIGNATURE))
                                    return $"Expecting transaction header_signature (after block {blockNum}) in {responseJson}";
                                var transactionId = (string)transactionObj[HEADER_SIGNATURE];
                                if (!transactionObj.ContainsKey(HEADER))
                                    return $"Expecting transaction header (after block {blockNum}) in {responseJson}";
                                var transactionHeader = (JObject)transactionObj[HEADER];

                                if (!transactionHeader.ContainsKey(FAMILY_NAME))
                                    return $"Expecting transaction family_name (after block {blockNum}) in {responseJson}";
                                var familyName = (string)transactionHeader[FAMILY_NAME];
                                if (!transactionHeader.ContainsKey(FAMILY_VERSION))
                                    return $"Expecting transaction family_version (after block {blockNum}) in {responseJson}";
                                var familyVersion = (string)transactionHeader[FAMILY_VERSION];
                                version = familyVersion;
                                if (!transactionHeader.ContainsKey(SIGNER_PUBLIC_KEY))
                                    return $"Expecting transaction signer_public_key (after block {blockNum}) in {responseJson}";
                                var transactionSignerPublicKey = (string)transactionHeader[SIGNER_PUBLIC_KEY];

                                var payloadBytes = Convert.FromBase64String((string)transactionObj[PAYLOAD]);
                                string payload;
                                string[] supportedVersions = { "1.0", "1.1", "1.2", "1.3", "1.4" };
                                if (familyName.Equals("CREDITCOIN") && Array.Exists(supportedVersions, v => v == familyVersion))
                                {
                                    var cbor = CBORObject.DecodeFromBytes(payloadBytes);
                                    var sb = new StringBuilder();
                                    var verb = cbor["v"].AsString();
                                    sb.Append(verb);
                                    for (int i = 1; i < cbor.Count; ++i)
                                    {
                                        var param = cbor[$"p{i}"].AsString();
                                        sb.Append(' ');
                                        sb.Append(param);
                                    }
                                    payload = sb.ToString();
                                }
                                else if (familyName.Equals("sawtooth_settings") && familyVersion.Equals("1.0"))
                                {
                                    Setting setting = Setting.Parser.ParseFrom(payloadBytes);
                                    payload = setting.ToString();
                                }
                                else
                                {
                                    payload = Encoding.ASCII.GetString(payloadBytes);
                                }

                                if (!transactionSignerPublicKey.All("1234567890abcdef".Contains))
                                    return $"Invalid transaction public key {transactionSignerPublicKey} (after block {blockNum}) in {responseJson}";
                                var compressionFlag = transactionSignerPublicKey.Substring(0, 2 * 1);
                                if (transactionSignerPublicKey.Length == 2 * 1 + 2 * 32 + 2 * 32 && compressionFlag.Equals("04"))
                                {
                                    // this is an uncompressed pub key, compress
                                    var yLast = transactionSignerPublicKey.Substring(2 * 1 + 2 * 32 + 2 * 31, 2 * 1);
                                    int value = int.Parse(yLast, System.Globalization.NumberStyles.HexNumber);
                                    transactionSignerPublicKey = ((value % 2 == 0) ? "02" : "03") + transactionSignerPublicKey.Substring(2 * 1, 2 * 32);
                                }
                                else if (transactionSignerPublicKey.Length != 2 * 1 + 2 * 32 || !compressionFlag.Equals("02") && !compressionFlag.Equals("03"))
                                {
                                    return $"Invalid transaction public key {transactionSignerPublicKey} (after block {blockNum}) in {responseJson}";
                                }

                                var transaction = new Models.Transaction
                                {
                                    FamilyName = familyName,
                                    FamilyVersion = familyVersion,
                                    Payload = payload,
                                    SignerPubKey = transactionSignerPublicKey,
                                    Sighash = sighashFromPubKey(transactionSignerPublicKey)
                                };
                                transactions.Add(transactionId, transaction);

                                if (!transactionHeader.ContainsKey(BATCHER_PUBLIC_KEY))
                                    return $"Expecting transaction batcher_public_key (after block {blockNum}) in {responseJson}";
                                size += ((string)transactionHeader[BATCHER_PUBLIC_KEY]).Length;
                                if (!transactionHeader.ContainsKey(PAYLOAD_SHA512))
                                    return $"Expecting transaction payload_sha512 (after block {blockNum}) in {responseJson}";
                                size += ((string)transactionHeader[PAYLOAD_SHA512]).Length;
                                if (!transactionHeader.ContainsKey(NONCE))
                                    return $"Expecting transaction nonce (after block {blockNum}) in {responseJson}";
                                size += ((string)transactionHeader[NONCE]).Length;
                                size += transactionId.Length;
                                size += ((string)transactionObj[PAYLOAD]).Length;
                                size += familyName.Length;
                                size += familyVersion.Length;
                                size += transactionSignerPublicKey.Length;
                                if (!transactionHeader.ContainsKey(INPUTS))
                                    return $"Expecting transaction inputs (after block {blockNum}) in {responseJson}";
                                var inputs = (JArray)transactionHeader[INPUTS];
                                foreach (var input in inputs)
                                    size += ((string)input).Length;
                                if (!transactionHeader.ContainsKey(OUTPUTS))
                                    return $"Expecting transaction outputs (after block {blockNum}) in {responseJson}";
                                var outputs = (JArray)transactionHeader[OUTPUTS];
                                foreach (var output in outputs)
                                    size += ((string)output).Length;
                            }

                            if (!batchObj.ContainsKey(HEADER))
                                return $"Expecting batch header (after block {blockNum}) in {responseJson}";
                            var batchHeader = (JObject)batchObj[HEADER];
                            if (!batchHeader.ContainsKey(SIGNER_PUBLIC_KEY))
                                return $"Expecting batch header signer_public_key (after block {blockNum}) in {responseJson}";
                            size += ((string)batchHeader[SIGNER_PUBLIC_KEY]).Length;
                            if (!batchHeader.ContainsKey(TRANSACTION_IDS))
                                return $"Expecting batch header transaction_ids (after block {blockNum}) in {responseJson}";
                            var transactionIds = (JArray)batchHeader[TRANSACTION_IDS];
                            foreach (var transactionId in transactionIds)
                                size += ((string)transactionId).Length;
                            if (!batchObj.ContainsKey(HEADER_SIGNATURE))
                                return $"Expecting batch header_signature (after block {blockNum}) in {responseJson}";
                        }

                        if (!blockHeader.ContainsKey(CONSENSUS))
                            return $"Expecting block consensus (after block {blockNum}) in {responseJson}";
                        var consensusBase64 = (string)blockHeader[CONSENSUS];
                        var consensus = Convert.FromBase64String(consensusBase64);
                        var cconsensusComponents = System.Text.Encoding.ASCII.GetString(consensus).Split(':');
                        string consensusName;
                        string timestamp = null;
                        string nonce = null;
                        string difficulty = null;
                        if (cconsensusComponents.Length == 4)
                        {
                            consensusName = cconsensusComponents[IDX_POW];
                            if (!consensusName.Equals("PoW"))
                                return $"Unexpected consensus name '{consensusName}' (after block {blockNum}, consensusBase64 '{consensusBase64}', consensus '{consensus}') in {responseJson}";
                            nonce = cconsensusComponents[IDX_NONCE];
                            difficulty = cconsensusComponents[IDX_DIFFICULTY];
                            int unused;
                            if (!int.TryParse(difficulty, out unused))
                                return $"Invalid numeric for difficulty '{difficulty}' (after block {blockNum}, consensusBase64 '{consensusBase64}', consensus '{consensus}') in {responseJson}";
                            var timestampString = cconsensusComponents[IDX_TIME];
                            double seconds;
                            if (!double.TryParse(timestampString, out seconds))
                                return $"Invalid numeric for timestamp '{timestampString}' (after block {blockNum}, consensusBase64 '{consensusBase64}', consensus '{consensus}') in {responseJson}";
                            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
                            dateTime = dateTime.AddSeconds(seconds).ToLocalTime();
                            timestamp = dateTime.ToString();
                        }
                        else if (cconsensusComponents.Length != 1)
                        {
                            return $"Unexpected consensus composition (after block {blockNum}, consensusBase64 '{consensusBase64}', consensus '{consensus}') in {responseJson}";
                        }
                        else
                        {
                            consensusName = cconsensusComponents[0];
                        }
                        if (!blockHeader.ContainsKey(PREVIOUS_BLOCK_ID))
                            return $"Expecting block previous_block_id (after block {blockNum}) in {responseJson}";
                        var previousBlockId = (string)blockHeader[PREVIOUS_BLOCK_ID];
                        if (!blockHeader.ContainsKey(SIGNER_PUBLIC_KEY))
                            return $"Expecting block signer_public_key (after block {blockNum}) in {responseJson}";
                        var blockSignerPublicKey = (string)blockHeader[SIGNER_PUBLIC_KEY];

                        if (!blockHeader.ContainsKey(BATCH_IDS))
                            return $"Expecting block batch_ids (after block {blockNum}) in {responseJson}";
                        var batchIds = (JArray)blockHeader[BATCH_IDS];
                        foreach (var batchId in batchIds)
                            size += ((string)batchId).Length;
                        size += blockNum.Length;
                        size += consensusBase64.Length;
                        size += previousBlockId.Length;
                        size += blockSignerPublicKey.Length;
                        size += ((string)blockHeader[STATE_ROOT_HASH]).Length;
                        size += blockId.Length;

                        var block = new Models.Block
                        {
                            BlockNum = blockNum,
                            Consensus = consensusName,
                            Difficulty = difficulty,
                            Nonce = nonce,
                            Timestamp = timestamp,
                            BlockReward = ccbe.Controllers.BlockchainController.calculateBlockReward(blockNum),
                            Version = version,
                            Size = size.ToString(),
                            PrevBlockId = previousBlockId,
                            SignerPubKey = blockSignerPublicKey,
                            Sighash = sighashFromPubKey(blockSignerPublicKey),
                            Transactions = transactions
                        };
                        newBlocks[blockIndex] = block;
                        newIds[blockIndex] = blockId;
                    }

                    if (done)
                        break;

                    if (!response.ContainsKey(PAGING))
                    {
                        return $"Expecting paging in {responseJson}";
                    }

                    var paging = (JObject)response[PAGING];
                    if (!paging.ContainsKey(NEXT))
                        break;
                    var next = (string)paging[NEXT];
                    url = next;
                }
            }
            catch (Exception x)
            {
                lock (success)
                    success.value = false;
                return $"Unexpected exception in UpdateCache() (after block {blockNum}): {x.Message}";
            }

            var height = blocks.Count;

            var newRange = new Models.Block[newBlocks.Count];

            foreach (var entry in newBlocks)
            {
                int index = entry.Key - height;
                if (entry.Key < height || entry.Key >= height + newBlocks.Count || newRange[index] != null)
                {
                    lock (success)
                        success.value = false;
                    return $"Unexpected error in UpdateCache() - out of order block {entry.Key} after block {height}";
                }
                newRange[index] = entry.Value;
            }

            lock (blocks)
            {
                blocks.AddRange(newRange);
                foreach (var entry in newIds)
                {
                    var index = entry.Key;
                    var id = entry.Value;
                    id2idx.Add(id, index);
                    idx2id.Add(index, id);
                }
                lock (success)
                    success.value = true;
            }

            return null;
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
