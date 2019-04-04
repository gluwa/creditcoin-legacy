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
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
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

        private int mConfirmationsExpected = 12;

        public bool Run(bool txid, IConfiguration cfg, HttpClient httpClient, ITxBuilder txBuilder, Dictionary<string, string> settings, string progressId, string pluginsFolder, string url, string[] command, out bool inProgress, out string msg)
        {
            Debug.Assert(command != null);
            if (command.Length < 2)
            {
                inProgress = false;
                msg = "invalid parameter count";
                return false;
            }

            bool erc20 = command[0].Equals("collectCoins", StringComparison.OrdinalIgnoreCase);
            if (erc20 || command[0].Equals("registerTransfer", StringComparison.OrdinalIgnoreCase))
            {
                // ethereum RegisterTransfer gain orderId
                // ethereum CollectCoins amount

                string progress = Path.Combine(pluginsFolder, $"{name}_progress{progressId}.txt");

                inProgress = false;

                if (erc20 && command.Length != 2 || !erc20 && command.Length != 3)
                {
                    msg = "invalid parameter count";
                    return false;
                }

                string gainString = "0";
                string orderId = null;
                string amountString = null;
                if (erc20)
                {
                    amountString = command[1];
                }
                else
                {
                    gainString = command[1];
                    orderId = command[2];
                }

                string secret = cfg["secret"];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    msg = "ethereum.secret is not set";
                    return false;
                }
                var ethereumPrivateKey = secret;

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";
                    return false;
                }

                string ethSrcAddress = EthECKey.GetPublicAddress(ethereumPrivateKey);
                var web3 = new Nethereum.Web3.Web3(rpcUrl);

                string srcAddressId = null;
                string dstAddressId = null;
                if (!erc20)
                {
                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{orderId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }
                    if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix))
                    {
                        var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                        if (gainString.Equals("0"))
                        {
                            srcAddressId = dealOrder.SrcAddress;
                            dstAddressId = dealOrder.DstAddress;
                        }
                        else
                        {
                            dstAddressId = dealOrder.SrcAddress;
                            srcAddressId = dealOrder.DstAddress;
                        }
                        amountString = dealOrder.Amount;
                    }
                    else if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix))
                    {
                        var repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                        if (gainString.Equals("0"))
                        {
                            srcAddressId = repaymentOrder.SrcAddress;
                            dstAddressId = repaymentOrder.DstAddress;
                        }
                        else
                        {
                            dstAddressId = repaymentOrder.SrcAddress;
                            srcAddressId = repaymentOrder.DstAddress;
                        }
                        amountString = repaymentOrder.Amount;
                    }
                    else
                    {
                        msg = "unexpected referred order";
                        return false;
                    }
                }

                string payTxId;
                if (File.Exists(progress))
                {
                    Console.WriteLine("Found unfinished action, retrying...");
                    payTxId = File.ReadAllText(progress);
                }
                else
                {
                    string ethDstAddress = null;
                    if (!erc20)
                    {
                        var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{srcAddressId}", out msg);
                        if (protobuf == null)
                        {
                            msg = "failed to extract address data through RPC";
                            return false;
                        }
                        var srcAddress = Address.Parser.ParseFrom(protobuf);

                        protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{dstAddressId}", out msg);
                        if (protobuf == null)
                        {
                            msg = "failed to extract address data through RPC";
                            return false;
                        }
                        Address dstAddress = Address.Parser.ParseFrom(protobuf);

                        if (!srcAddress.Blockchain.Equals(name) || !dstAddress.Blockchain.Equals(name))
                        {
                            msg = $"ethereum RegisterTransfer can only transfer ether.\nThis source is registered for {srcAddress.Blockchain} and destination for {dstAddress.Blockchain}";
                            return false;
                        }
                        ethDstAddress = dstAddress.Value;

                        if (!ethSrcAddress.Equals(srcAddress.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            msg = "The deal is for a different client";
                            return false;
                        }
                    }

                    BigInteger transferAmount;
                    if (!BigInteger.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    BigInteger gain;
                    if (!BigInteger.TryParse(gainString, out gain))
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    transferAmount = transferAmount + gain;
                    if (transferAmount < 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }

                    var txCount = web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(ethSrcAddress).Result;
                    TransactionSigner signer = new TransactionSigner();

                    HexBigInteger gasPrice;

                    string gasPriceInGweiString = cfg["gasPriceInGwei"];
                    if (int.TryParse(gasPriceInGweiString, out int gasPriceOverride))
                    {
                        gasPrice = new HexBigInteger(Nethereum.Util.UnitConversion.Convert.ToWei(gasPriceOverride, Nethereum.Util.UnitConversion.EthUnit.Gwei));
                    }
                    else
                    {
                        gasPrice = web3.Eth.GasPrice.SendRequestAsync().Result;
                    }
                    Console.WriteLine("gasPrice: " + gasPrice.Value.ToString());

                    string to;
                    string data;
                    BigInteger amount;
                    HexBigInteger gasLimit;
                    if (erc20)
                    {
                        string creditcoinContract = cfg["creditcoinContract"];
                        string creditcoinContractAbi = cfg["creditcoinContractAbi"];

                        to = creditcoinContract;

                        var contract = web3.Eth.GetContract(creditcoinContractAbi, creditcoinContract);
                        var burn = contract.GetFunction("exchange");
                        var functionInput = new object[] { transferAmount, txBuilder.getSighash() };
                        data = burn.GetData(functionInput);
                        gasLimit = burn.EstimateGasAsync(functionInput).Result;
                        amount = 0;
                    }
                    else
                    {
                        gasLimit = web3.Eth.Transactions.EstimateGas.SendRequestAsync(new Nethereum.RPC.Eth.DTOs.CallInput(orderId, ethDstAddress, new Nethereum.Hex.HexTypes.HexBigInteger(transferAmount))).Result;
                        Console.WriteLine("gasLimit: " + gasLimit.Value.ToString());

                        to = ethDstAddress;
                        data = orderId;
                        amount = transferAmount;
                    }

                    string txRaw = signer.SignTransaction(ethereumPrivateKey, to, amount, txCount, gasPrice, gasLimit, data);
                    payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;
                    Console.WriteLine("Ethereum Transaction ID: " + payTxId);

                    File.WriteAllText(progress, $"{payTxId}");
                }

                inProgress = true;
                while (true)
                {
                    var receipt = web3.Eth.TransactionManager.TransactionReceiptService.PollForReceiptAsync(payTxId).Result;
                    if (receipt.BlockNumber != null)
                    {
                        var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                        if (blockNumber.Value - receipt.BlockNumber.Value >= mConfirmationsExpected)
                        {
                            break;
                        }
                    }
                    Thread.Sleep(1000);
                }
                File.Delete(progress);
                inProgress = false;

                if (erc20)
                {
                    command = new string[] { command[0], ethSrcAddress, amountString, payTxId };
                }
                else
                {
                    command = new string[] { command[0], gainString, orderId, payTxId };
                }
                var tx = txBuilder.BuildTx(command, out msg);
                if (tx == null)
                {
                    return false;
                }

                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, url, "batches", content, txid);

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