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
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;
using PeterO.Cbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace cbitcoin
{
    public class Bitcoin : ICCClientPlugin
    {
        private const string name = "bitcoin";

        private const string STATE = "s";
        private const string NEW = "n";
        private const string FOUNDED = "f";
        private const string SOURCE_LEDGER = "sl";
        private const string SOURCE_ID = "si";
        private const string SOURCE_AMOUNT = "sa";
        private const string DESTINATION_LEDGER = "dl";
        private const string DESTINATION_ID = "di";
        private const string DESTINATION_AMOUNT = "da";

        public bool Run(IConfiguration cfg, HttpClient httpClient, ITxBuilder txBuilder, Dictionary<string, string> settings, string pluginsFolder, string url, string[] command, out bool inProgress, out string msg)
        {
            Debug.Assert(command != null);
            Debug.Assert(command.Length > 1);
            if (command[0].Equals("registerTransfer", StringComparison.OrdinalIgnoreCase))
            {
                // bitcoin RegisterTransfer registeredSourceId amount sourceTxId

                string progress = Path.Combine(pluginsFolder, $"{name}_progress.txt");

                inProgress = false;

                Debug.Assert(command.Length == 4);
                string registeredSourceId = command[1];
                string amountString = command[2];
                string sourceTxIdString = command[3];

                string secret = cfg["secret"];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    msg = "bitcoin.secret is not set";
                    return false;
                }
                string rpcAddress = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcAddress))
                {
                    msg = "bitcoin.rpc is not set";
                    return false;
                }

                string credential = cfg["credential"];
                if (string.IsNullOrWhiteSpace(credential))
                {
                    msg = "bitcoin.credential is not set";
                    return false;
                }

                //TODO disable confirmation count config for release build
                string confirmationsCount = cfg["confirmationsCount"];
                if (string.IsNullOrWhiteSpace(confirmationsCount))
                {
                    msg = "bitcoin.confirmationsCount is not set";
                    return false;
                }
                int confirmationsExpected = 1;
                if (!int.TryParse(confirmationsCount, out confirmationsExpected))
                {
                    msg = "bitcoin.confirmationsCount is not an int";
                    return false;
                }

                string feeString = cfg["fee"];
                if (string.IsNullOrWhiteSpace(feeString))
                {
                    msg = "bitcoin.fee is not set";
                    return false;
                }
                if (!int.TryParse(feeString, out int fee))
                {
                    msg = "bitcoin.fee is not an int";
                    return false;
                }

                var bitcoinPrivateKey = new BitcoinSecret(secret);
                var network = bitcoinPrivateKey.Network;
                var rpcClient = new RPCClient(credential, new Uri(rpcAddress), network);

                string payTxIdHash;
                uint256 payTxId;
                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    payTxIdHash = File.ReadAllText(progress);
                    if (!uint256.TryParse(payTxIdHash, out payTxId))
                    {
                        msg = "corrupted progress file";
                        return false;
                    }
                }
                else
                {
                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{registeredSourceId}", out msg);
                    if (protobuf == null)
                    {
                        return false;
                    }

                    var address = Address.Parser.ParseFrom(protobuf);

                    string destinationAddress;
                    if (!settings.TryGetValue("sawtooth.escrow." + name, out destinationAddress))
                    {
                        msg = "Escrow not found for " + name;
                        return false;
                    }

                    Money transferAmount;
                    if (!Money.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }

                    if (!address.Blockchain.Equals(name))
                    {
                        msg = $"bitcoin RegisterTransfer can only transfer bitcoins.\nThis source is registered for {address.Blockchain}";
                        return false;
                    }

                    var sourceAddress = bitcoinPrivateKey.GetAddress();
                    if (!sourceAddress.ToString().Equals(address.Address_, StringComparison.OrdinalIgnoreCase))
                    {
                        msg = "The deal is for a different client";
                        return false;
                    }

#if RELEASE
                    if (network != NBitcoin.Network.Main)
                    {
                        msg = "bitcoin.secret is not defined for main network";
                        return false;
                    }
#endif

                    var sourceTxId = uint256.Parse(sourceTxIdString);
                    var transactionResponse = rpcClient.GetRawTransaction(sourceTxId);

                    //TODO: fix (assuming that the transaction has one output intended for the private key with the exact amount)
                    var receivedCoins = transactionResponse.Outputs.AsCoins();
                    OutPoint outPointToSpend = null;
                    TxOut outTxToSpend = null;
                    Money amount = null;
                    foreach (var coin in receivedCoins)
                    {
                        if (coin.TxOut.ScriptPubKey == bitcoinPrivateKey.ScriptPubKey)
                        {
                            outPointToSpend = coin.Outpoint;
                            outTxToSpend = coin.TxOut;
                            amount = (Money)coin.Amount;
                            if (amount.CompareTo(transferAmount + fee * 2) < 0)
                            {
                                msg = $"Invalid transaction - needed: {transferAmount}, has: {amount.ToString()}";
                                return false;
                            }
                            break;
                        }
                    }
                    if (outPointToSpend == null)
                    {
                        msg = "Invalid transaction - no outputs that the client can spend";
                        return false;
                    }

                    var addressFrom = BitcoinAddress.Create(address.Address_, network);
                    var addressTo = BitcoinAddress.Create(destinationAddress, network);
                    var message = address.Sighash;
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var bitcoinTransactionBuilder = new TransactionBuilder();

                    var transaction = bitcoinTransactionBuilder
                        .AddCoins(new Coin(outPointToSpend, outTxToSpend))
                        .AddKeys(bitcoinPrivateKey)
                        .Send(addressTo.ScriptPubKey, transferAmount + fee)
                        .Send(TxNullDataTemplate.Instance.GenerateScriptPubKey(bytes), Money.Zero)
                        .SendFees(new Money(fee, MoneyUnit.Satoshi))
                        .SetChange(addressFrom.ScriptPubKey)
                        .BuildTransaction(true);

                    if (!bitcoinTransactionBuilder.Verify(transaction))
                    {
                        msg = "failed verify transaction";
                        return false;
                    }

                    try
                    {
                        payTxId = rpcClient.SendRawTransaction(transaction);
                    }
                    catch (RPCException e)
                    {
                        msg = $"failed to broadcast - error: {e.RPCCode}, reason: {e.RPCCodeMessage}";
                        return false;
                    }

                    if (payTxId == null)
                    {
                        msg = "failed to broadcast - unknown error";
                        return false;
                    }

                    payTxIdHash = payTxId.ToString();
                    File.WriteAllText(progress, payTxIdHash);
                }

                inProgress = true;
                while (true)
                {
                    var transactionResponse = rpcClient.GetRawTransactionInfo(payTxId);
                    if (transactionResponse != null && transactionResponse.BlockHash != null && transactionResponse.Confirmations >= confirmationsExpected)
                    {
                        break;
                    }

                    Thread.Sleep(1000);
                }

                command = new string[] { command[0], registeredSourceId, amountString, feeString, payTxIdHash, ((network == Network.Main) ? "1" : "0") };
                var tx = txBuilder.BuildTx(command, out msg);
                Debug.Assert(tx != null);
                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, $"{url}/batches", content);

                File.Delete(progress);
                inProgress = false;

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