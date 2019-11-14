/*
    Copyright(c) 2018 Gluwa, Inc.

    This file is part of Creditcoin.

    Creditcoin is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU Lesser General Public License for more details.
    
    You should have received a copy of the GNU Lesser General Public License
    along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace ccplugin
{
    public static class RpcHelper
    {
        private const string DATA = "data";
        private const string LINK = "link";
        private const string STATUS = "status";
        private const string ID = "id";
        private const string TRANSACTIONS = "transactions";
        private const string HEADER_SIGNATURE = "header_signature";
        private const string ERROR = "error";
        private const string MESSAGE = "message";
        private const string HEADER = "header";
        private const string BLOCK_NUM = "block_num";

        public static string creditCoinNamespace = "8a1a04";
        public static string settingNamespace = "000000";
        public static string walletPrefix = "0000";
        public static string addressPrefix = "1000";
        public static string transferPrefix = "2000";
        public static string askOrderPrefix = "3000";
        public static string bidOrderPrefix = "4000";
        public static string dealOrderPrefix = "5000";
        public static string repaymentOrderPrefix = "6000";
        public static string offerPrefix = "7000";

        public static string CompleteBatch(HttpClient httpClient, string host, string path, ByteArrayContent content, bool txid, out string continuation)
        {
            continuation = null;
            string url = $"{host}/{path}";
            using (var responseMessage = httpClient.PostAsync(url, content).Result)
            {
                var json = responseMessage.Content.ReadAsStringAsync().Result;
                var response = JObject.Parse(json);
                if (response.ContainsKey(ERROR))
                {
                    var error = (JObject)response[ERROR];
                    if (!error.ContainsKey(MESSAGE))
                    {
                        return "Error: message is missing in " + json;
                    }
                    var message = (string)error[MESSAGE];
                    return "Error: " + message;
                }
                if (!response.ContainsKey(LINK))
                {
                    return "Error: link is missing in " + json;
                }
                string link = (string)response[LINK];
                var ret = CompleteBatch(httpClient, host, link, txid);
                if (ret == null)
                    continuation = link;
                return ret;
            }
        }

        public static string CompleteBatch(HttpClient httpClient, string host, string link, bool txid)
        {
            using (var linkResponseMessage = httpClient.GetAsync(link).Result)
            {
                var json = linkResponseMessage.Content.ReadAsStringAsync().Result;
                var response = JObject.Parse(json);
                if (response.ContainsKey(ERROR))
                {
                    var error = (JObject)response[ERROR];
                    if (!error.ContainsKey(MESSAGE))
                    {
                        return "Error: message is missing in " + json;
                    }
                    var message = (string)error[MESSAGE];
                    return "Error: " + message;
                }
                else
                {
                    if (!response.ContainsKey(DATA))
                    {
                        return "Error: data is missing in " + json;
                    }
                    var data = (JArray)response[DATA];
                    if (data.Count != 1)
                    {
                        return "Error: expecting a single item for data in " + json;
                    }
                    var obj = (JObject)data[0];
                    if (!obj.ContainsKey(STATUS))
                    {
                        return "Error: status is missing in " + json;
                    }
                    var status = (string)obj[STATUS];
                    if (status.Equals("INVALID"))
                    {
                        return "Error: request rejected";
                    }
                    else if (status.Equals("COMMITTED"))
                    {
                        if (txid)
                        {
                            if (obj.ContainsKey(ID))
                            {
                                var id = (string)obj[ID];
                                string url = $"{host}/batches?id={id}";
                                using (var batchResponseMessage = httpClient.GetAsync(url).Result)
                                {
                                    json = batchResponseMessage.Content.ReadAsStringAsync().Result;
                                    response = JObject.Parse(json);
                                    if (response.ContainsKey(DATA))
                                    {
                                        data = (JArray)response[DATA];
                                        if (data.Count == 1)
                                        {
                                            obj = (JObject)data[0];
                                            if (obj.ContainsKey(TRANSACTIONS))
                                            {
                                                var transactions = (JArray)obj[TRANSACTIONS];
                                                if (transactions.Count == 1)
                                                {
                                                    var transaction = (JObject)transactions[0];
                                                    if (transaction.ContainsKey(HEADER_SIGNATURE))
                                                    {
                                                        return "Success, txid: " + (string)transaction[HEADER_SIGNATURE];
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            return "Success";
        }

        public static string LastBlock(HttpClient httpClient, string restApiUrl, out string msg)
        {
            var responseMessage = httpClient.GetAsync(restApiUrl + "/blocks?limit=1").Result;
            var responseJson = responseMessage.Content.ReadAsStringAsync().Result;
            var response = JObject.Parse(responseJson);
            if (response.ContainsKey(ERROR))
            {
                msg = (string)response[ERROR][MESSAGE];
                return null;
            }
            if (!response.ContainsKey(DATA))
            {
                msg = "Expecting data in " + responseJson;
                return null;
            }
            var data = (JArray)response[DATA];
            if (data.Count != 1)
            {
                msg = "Expecting a single item for data in " + responseJson;
                return null;
            }
            var block = (JObject)data[0];
            if (!block.ContainsKey(HEADER))
            {
                msg = "Expecting a header in " + responseJson;
                return null;
            }
            var header = (JObject)block[HEADER];
            if (!header.ContainsKey(BLOCK_NUM))
            {
                msg = "Expecting a block_num in " + responseJson;
                return null;
            }
            msg = null;
            return (string)header[BLOCK_NUM];
        }

        public static byte[] ReadProtobuf(HttpClient httpClient, string url, out string msg)
        {
            var response = httpClient.GetAsync(url).Result;
            var dealStateJson = response.Content.ReadAsStringAsync().Result;
            var dealStateObj = JObject.Parse(dealStateJson);
            if (dealStateObj.ContainsKey(ERROR))
            {
                msg = (string)dealStateObj[ERROR][MESSAGE];
                return null;
            }

            Debug.Assert(dealStateObj.ContainsKey(DATA));
            string data = (string)dealStateObj[DATA];
            byte[] protobuf = Convert.FromBase64String(data);
            msg = null;
            return protobuf;
        }

        public static byte[] HexToBytes(string input)
        {
            if (input.Length % 2 != 0)
            {
                throw new Exception("Invalid hex string");
            }
            var outputLength = input.Length / 2;
            var output = new byte[outputLength];
            for (var i = 0; i < outputLength; i++)
            {
                output[i] = Convert.ToByte(input.Substring(i * 2, 2), 16);
            }
            return output;
        }
    }
}