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

using ccplugin;
using Microsoft.Extensions.Configuration;
using Nethereum.Signer;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Threading;

namespace cethereum
{
    public class Ethereum : ICCClientPlugin
    {
        private const string name = "ethereum";

        private const string STATE = "s";
        private const string NEW = "n";
        private const string FOUNDED = "f";
        private const string SOURCE_LEDGER = "sl";
        private const string SOURCE_ID = "si";
        private const string SOURCE_AMOUNT = "sa";
        private const string DESTINATION_LEDGER = "dl";
        private const string DESTINATION_ID = "di";
        private const string DESTINATION_AMOUNT = "da";

        private const string ERROR = "error";
        private const string DATA = "data";
        private const string MESSAGE = "message";

        public bool Run(IConfiguration cfg, HttpClient httpClient, ITxBuilder txBuilder, Dictionary<string, string> settings, string pluginsFolder, string url, string[] command, out bool inProgress, out string msg)
        {
            Debug.Assert(command != null);
            Debug.Assert(command.Length > 1);
            if (command[0].Equals("registerTransfer", StringComparison.OrdinalIgnoreCase))
            {
                // ethereum RegisterTransfer registeredSourceId amount

                string progress = Path.Combine(pluginsFolder, $"{name}_progress.txt");

                inProgress = false;

                Debug.Assert(command.Length == 3 || command.Length == 4);
                string registeredSourceId = command[1];
                string amountString = command[2];
                bool erc20 = command.Length == 4;

                string secret = cfg["secret"];
                if (string.IsNullOrEmpty(secret))
                {
                    msg = "ethereum.secret is not set";

                    return false;
                }
                var ethereumPrivateKey = secret;

                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrEmpty(confirmationsCount))
                {
                    msg = "ethereum.confirmationsCount is not set";

                    return false;
                }
                if (!int.TryParse(confirmationsCount, out int confirmationsExpected))
                {
                    msg = "ethereum.confirmationsCount is not an int";

                    return false;
                }

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrEmpty(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";

                    return false;
                }

                var web3 = new Nethereum.Web3.Web3(rpcUrl);

                string payTxId;
                BigInteger fee;
                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    var data = File.ReadAllText(progress).Split(':');
                    if (data.Length != 2)
                    {
                        msg = "Invalid progress data";
                        return false;
                    }
                    payTxId = data[0];
                    if (!BigInteger.TryParse(data[1], out fee))
                    {
                        msg = "Invalid progress data";
                        return false;
                    }
                }
                else
                {
                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{registeredSourceId}", out msg);
                    if (protobuf == null)
                        return false;
                    var address = Address.Parser.ParseFrom(protobuf);

                    string destinationAddress;
                    if (!settings.TryGetValue("sawtooth.escrow." + name, out destinationAddress))
                    {
                        msg = "Escrow not found for " + name;
                        return false;
                    }

                    BigInteger transferAmount;
                    if (!BigInteger.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }

                    if (!address.Blockchain.Equals(name))
                    {
                        msg = $"ethereum RegisterTransfer can only transfer ether.\nThis source is registered for {address.Blockchain}";
                        return false;
                    }

                    string sourceAddress = EthECKey.GetPublicAddress(ethereumPrivateKey);

                    if (!sourceAddress.Equals(address.Address_, StringComparison.OrdinalIgnoreCase))
                    {
                        msg = "The deal is for a different client";
                        return false;
                    }

                    var txCount = web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(sourceAddress).Result;
                    TransactionSigner signer = new TransactionSigner();

                    // TODO maybe add a y/n choice for user to decline or configurable gas price override
                    var gasLimit = web3.Eth.Transactions.EstimateGas.SendRequestAsync(new Nethereum.RPC.Eth.DTOs.CallInput(registeredSourceId, destinationAddress, new Nethereum.Hex.HexTypes.HexBigInteger(transferAmount))).Result;
                    var gasPrice = web3.Eth.GasPrice.SendRequestAsync().Result;
                    Console.WriteLine("gasLimit: " + gasLimit.Value.ToString());
                    Console.WriteLine("gasPrice: " + gasPrice.Value.ToString());

                    fee = gasLimit.Value * gasPrice.Value;

                    string to;
                    string data;
                    BigInteger amount;
                    if (erc20)
                    {
                        string creditcoinContract = cfg["creditcoinContract"];
                        string creditcoinContractAbi = cfg["creditcoinContractAbi"];

                        to = creditcoinContract;

                        var contract = web3.Eth.GetContract(creditcoinContractAbi, creditcoinContract);
                        var burn = contract.GetFunction("burn");
                        data = burn.GetData(new object[] { transferAmount, address.Sighash });
                        amount = 0;
                    }
                    else
                    {
                        to = destinationAddress;
                        data = address.Sighash;
                        amount = transferAmount + fee;
                    }

                    string txRaw = signer.SignTransaction(ethereumPrivateKey, to, amount, txCount, gasPrice, gasLimit, data);
                    payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;
                    Console.WriteLine("Ethereum Transaction ID: " + payTxId);

                    File.WriteAllText(progress, $"{payTxId}:{fee.ToString()}");
                }

                inProgress = true;
                for (; ; )
                {
                    var receipt = web3.Eth.TransactionManager.TransactionReceiptService.PollForReceiptAsync(payTxId).Result;
                    if (receipt.BlockNumber != null)
                    {
                        var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                        if (blockNumber.Value - receipt.BlockNumber.Value >= confirmationsExpected)
                            break;
                    }
                    Thread.Sleep(1000);
                }
                File.Delete(progress);
                inProgress = false;

                command = new string[] { command[0], registeredSourceId, amountString, erc20? "0": fee.ToString(), payTxId, erc20? "creditcoin": "1" };
                var tx = txBuilder.BuildTx(command, out msg);
                if (tx == null)
                    return false;

                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, $"{url}/batches", content);

                return true;
            }
            else
            {
                inProgress = false;
                msg = "Unknown command: " + command[0];
                return false;
            }
        }
    }
}
