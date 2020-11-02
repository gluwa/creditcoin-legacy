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
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;

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

        public bool Run(bool txid, IConfiguration cfg, string secretOverride, HttpClient httpClient, ITxBuilder txBuilder, ref string progressToken, string url, string[] command, out bool inProgress, out string msg, out string link)
        {
            link = null;

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

                inProgress = false;

                if (erc20 && command.Length != 2 || !erc20 && command.Length != 3)
                {
                    msg = "invalid parameter count";
                    return false;
                }

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";
                    return false;
                }
                var web3 = new Nethereum.Web3.Web3(rpcUrl);

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                string payTxId;
                string ethSrcAddress = null;
                string amountString = null;
                string gainString = null;
                string orderId = null;
                if (progressToken != null)
                {
                    var progressTokenComponents = progressToken.Split(':');
                    Debug.Assert(progressTokenComponents.Length == 3);

                    payTxId = progressTokenComponents[0];

                    if (erc20)
                    {
                        ethSrcAddress = progressTokenComponents[1];
                        amountString = progressTokenComponents[2];
                    }
                    else
                    {
                        gainString = progressTokenComponents[1];
                        orderId = progressTokenComponents[2];
                    }
                }
                else
                {
                    gainString = "0";
                    if (erc20)
                    {
                        amountString = command[1];
                    }
                    else
                    {
                        gainString = command[1];
                        orderId = command[2];
                    }

                    string ethereumPrivateKey = secretOverride ?? cfg["secret"];
                    if (string.IsNullOrWhiteSpace(ethereumPrivateKey))
                    {
                        msg = "ethereum.secret is not set";
                        return false;
                    }
                    ethSrcAddress = EthECKey.GetPublicAddress(ethereumPrivateKey);

                    string srcAddressId = null;
                    string dstAddressId = null;
                    if (!erc20)
                    {
                        var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{orderId}", out msg);
                        if (protobuf == null)
                        {
                            msg = $"failed to extract address data through RPC: {msg}";
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

                    string ethDstAddress = null;
                    if (!erc20)
                    {
                        var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{srcAddressId}", out msg);
                        if (protobuf == null)
                        {
                            msg = $"failed to extract address data through RPC: {msg}";
                            return false;
                        }
                        var srcAddress = Address.Parser.ParseFrom(protobuf);

                        protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{dstAddressId}", out msg);
                        if (protobuf == null)
                        {
                            msg = $"failed to extract address data through RPC: {msg}";
                            return false;
                        }
                        Address dstAddress = Address.Parser.ParseFrom(protobuf);

                        if (!srcAddress.Blockchain.Equals(name) || !dstAddress.Blockchain.Equals(name))
                        {
                            msg = $"ethereum RegisterTransfer can only transfer ether.\nThis source is registered for {srcAddress.Blockchain} and destination for {dstAddress.Blockchain}";
                            return false;
                        }
                        ethDstAddress = dstAddress.Value;

                        if (!srcAddress.Network.Equals(dstAddress.Network))
                        {
                            msg = $"ethereum RegisterTransfer can only transfer ether on the same network.\nThis source is registered for {srcAddress.Network} and destination for {dstAddress.Network}";
                            return false;
                        }

                        if (!ethSrcAddress.Equals(srcAddress.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            msg = $"The deal is for a different client. Expected {ethSrcAddress}, got {srcAddress.Value}";
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
                        msg = "Invalid gain";
                        return false;
                    }
                    transferAmount = transferAmount + gain;
                    if (transferAmount < 0)
                    {
                        msg = "Overflow";
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

                        to = ethDstAddress;
                        data = orderId;
                        amount = transferAmount;
                    }

                    string txRaw = signer.SignTransaction(ethereumPrivateKey, to, amount, txCount, gasPrice, gasLimit, data);
                    payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;

                    if (erc20)
                    {
                        progressToken = $"{payTxId}:{ethSrcAddress}:{amountString}";
                    }
                    else
                    {
                        progressToken = $"{payTxId}:{gainString}:{orderId}";
                    }
                }

                inProgress = true;

                var receipt = web3.Eth.TransactionManager.TransactionReceiptService.PollForReceiptAsync(payTxId).Result;
                if (receipt.BlockNumber != null)
                {
                    if (receipt.Status.Value == 0)
                    {
                        msg = $"Failed transaction {progressToken}";
                        return false;
                    }

                    while (true)
                    {
                        var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                        if (blockNumber.Value - receipt.BlockNumber.Value >= mConfirmationsExpected)
                        {
                            progressToken = null;
                            break;
                        }

                        System.Threading.Thread.Sleep(10_000);
                    }
                }

                if (progressToken != null)
                {
                    msg = null;
                    return true;
                }

                if (erc20)
                {
                    command = new string[] { command[0], ethSrcAddress, amountString, payTxId };
                }
                else
                {
                    command = new string[] { command[0], gainString, orderId, payTxId };
                }

                var tx = txBuilder.BuildTx(command, out msg);
                Debug.Assert(tx != null);
                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, url, "batches", content, txid, out link);

                inProgress = false;

                if (msg != null)
                {
                    link = null;
                }
                else
                {
                    Debug.Assert(link != null);
                }

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