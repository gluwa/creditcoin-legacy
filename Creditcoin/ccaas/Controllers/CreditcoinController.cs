using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Sawtooth.Sdk.Client;

namespace ccaas.Controllers
{
    [Route("api")]
    [ApiController]
    public class CreditcoinController : Controller
    {
        public static IConfiguration config;
        public static string pluginFolder;
        public static string creditcoinUrl;
        public static BigInteger minimalFee;
        public static HttpClient httpClient = new HttpClient();

        private const string keyIsMissing = "Key is missing";
        private const string missingParameters = "Missing required parameter(s)";
        private const string unexpectedError = "Error (unexpected)";
        private const string invalidSighash = "Invalid sighash format, expecting 60 hexadecimal digits";

        [HttpGet("MinimalEthlessFee")]
        public IActionResult GetMinimalEthlessFee()
        {
            return Ok(minimalFee.ToString());
        }

        [HttpGet("RetrieveAccount/{sighash}")]
        public IActionResult GetRetrieveAccount(string sighash)
        {
            //show Balance sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Balance");
            args.Add(sighash);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count != 2 || output[0].StartsWith("Error") || !output[1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new ccaas.Models.Account() { amount = output[0] };
            return Json(ret);
        }

        private static string ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return keyIsMissing;
            if (key.Length != 64 || !key.All("1234567890abcdef".Contains))
                return "Invalid key format, expecting 64 hexadecimal digits";
            return null;
        }

        [HttpPost("Sighash")]
        public IActionResult PostSighash(Models.SighashQueryParam queryParam)
        {
            //sighash
            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);
            var sighash = ccplugin.TxBuilder.getSighash(signer);

            return Ok(sighash);
        }

        [HttpGet("CheckCommittedStatus")]
        public IActionResult GetCheckIfCommitted(string link)
        {
            var msg = cccore.Core.Run(httpClient, creditcoinUrl, HttpUtility.UrlDecode(link), false);
            if (msg == null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = link });
            if (msg == "Unknown batch ID")
                return StatusCode(404, msg);
            if (!msg.Equals("Success"))
                return statusCodeByMsg(msg);
            return Ok();
        }

        [HttpPost("SendFunds")]
        public IActionResult PostSendFunds(Models.SendFundsQueryParam queryParam)
        {
            //creditcoin SendFunds amount sighash
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("SendFunds");
            args.Add(queryParam.query.amount);
            args.Add(queryParam.query.destinationSighash);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("RegisterAddress")]
        public IActionResult PostRegisterAddress(Models.RegisterAddressQueryParam queryParam)
        {
            //creditcoin RegisterAddress blockchain address network
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("RegisterAddress");
            args.Add(queryParam.query.blockchain);
            if (queryParam.query.blockchain.Equals("erc20") || queryParam.query.blockchain.Equals("ethless"))
            {
                if (string.IsNullOrWhiteSpace(queryParam.query.erc20))
                    return BadRequest("expecting erc20 parameter for erc20 and ethless blockchains");
                args.Add($"{queryParam.query.erc20}@{queryParam.query.address}");
            }
            else
            {
                args.Add(queryParam.query.address);
            }
            args.Add(queryParam.query.network);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("ShowAddress/{sighash}")]
        public IActionResult GetShowAddress(string sighash, string blockchain, string address, string network, string erc20 = null)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Address");
            args.Add(sighash);
            args.Add(blockchain);
            if (blockchain.Equals("erc20") || blockchain.Equals("ethless"))
            {
                if (string.IsNullOrWhiteSpace(erc20))
                    return BadRequest(missingParameters);
                args.Add($"{erc20}@{address}");
            }
            else
            {
                args.Add(address);
            }
            args.Add(network);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);

            // core returns success but no addresses in output
            if (output.Count == 1 && output[0].Equals("Success"))
            {
                return NotFound("Address with provided parameters not found.");
            }

            if (output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok(output[0]);
        }

        [HttpPost("CreateAskOrder")]
        public IActionResult PostCreateAskOrder(Models.CreateOrderQueryParam queryParam)
        {
            //creditcoin AddAskOrder addressId amount interest maturity fee expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddAskOrder");
            args.Add(queryParam.query.addressId);
            args.Add(queryParam.query.term.amount);
            args.Add(queryParam.query.term.interest);
            args.Add(queryParam.query.term.maturity);
            args.Add(queryParam.query.term.fee);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("CreateBidOrder")]
        public IActionResult PostCreateBidOrder(Models.CreateOrderQueryParam queryParam)
        {
            //creditcoin AddBidOrder addressId amount interest maturity fee expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddBidOrder");
            args.Add(queryParam.query.addressId);
            args.Add(queryParam.query.term.amount);
            args.Add(queryParam.query.term.interest);
            args.Add(queryParam.query.term.maturity);
            args.Add(queryParam.query.term.fee);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        private static string getValue(string offerComponent, string expectedName)
        {
            var idx = offerComponent.IndexOf(':');
            if (idx == -1)
                throw new Exception();
            if (!offerComponent.Substring(0, idx).Equals(expectedName))
                throw new Exception();
            return cccore.Core.unquote(offerComponent.Substring(idx + 1));
        }

        [HttpGet("MatchingOrders/{sighash}")]
        public IActionResult GetMatchingOrders(string sighash)
        {
            //show MatchingOrders sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("MatchingOrders");
            args.Add(sighash);
            var askAndBidOrders = new cccore.Core.AskAndBidOrders();
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link, askAndBidOrders);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                var ret = new List<ccaas.Models.MatchingOrders>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    var ids = output[i].Split(' ');
                    if (ids.Length != 2)
                        return StatusCode(503, unexpectedError);

                    var askOrderId = ids[0];
                    var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                    {
                        var value = askAndBidOrders.askOrders[askOrderId];
                        askOrder.order.addressId = value.Address;
                        askOrder.order.term.amount = value.Amount;
                        askOrder.order.term.interest = value.Interest;
                        askOrder.order.term.maturity = value.Maturity;
                        askOrder.order.term.fee = value.Fee;
                        askOrder.order.expiration = value.Expiration.ToString();
                        askOrder.block = value.Block;
                    }

                    var bidOrderId = ids[1];
                    var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                    {
                        var value = askAndBidOrders.bidOrders[bidOrderId];
                        bidOrder.order.addressId = value.Address;
                        bidOrder.order.term.amount = value.Amount;
                        bidOrder.order.term.interest = value.Interest;
                        bidOrder.order.term.maturity = value.Maturity;
                        bidOrder.order.term.fee = value.Fee;
                        bidOrder.order.expiration = value.Expiration.ToString();
                        bidOrder.block = value.Block;
                    }

                    var matchingOrders = new ccaas.Models.MatchingOrders() { askOrder = askOrder, bidOrder = bidOrder };
                    ret.Add(matchingOrders);
                }
                return Json(ret);
            }
            catch (Exception)
            {
                return StatusCode(503, unexpectedError);
            }
        }

        [HttpPost("CreateOffer")]
        public IActionResult PostCreateOffer(Models.CreateOfferQueryParam queryParam)
        {
            //creditcoin AddOffer askOrderId bidOrderId expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddOffer");
            args.Add(queryParam.query.askOrderId);
            args.Add(queryParam.query.bidOrderId);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("RetrieveOffers/{sighash}")]
        public IActionResult GetRetrieveOffers(string sighash)
        {
            //show CurrentOffers sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("CurrentOffers");
            args.Add(sighash);
            var offersAndAskAndBidOrders = new cccore.Core.OffersAndAskAndBidOrders();
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link, offersAndAskAndBidOrders);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                var ret = new List<ccaas.Models.OfferResponse>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    string askOrderId, bidOrderId;
                    var offer = new ccaas.Models.OfferResponse() { id = output[i] };
                    {
                        var value = offersAndAskAndBidOrders.offers[output[i]];
                        askOrderId = value.AskOrder;
                        bidOrderId = value.BidOrder;
                        offer.expiration = value.Expiration.ToString();
                        offer.block = value.Block;
                    }

                    var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                    {
                        var value = offersAndAskAndBidOrders.askOrders[askOrderId];
                        askOrder.order.addressId = value.Address;
                        askOrder.order.term.amount = value.Amount;
                        askOrder.order.term.interest = value.Interest;
                        askOrder.order.term.fee = value.Fee;
                        askOrder.order.expiration = value.Expiration.ToString();
                        askOrder.block = value.Block;
                    }

                    var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                    {
                        var value = offersAndAskAndBidOrders.bidOrders[bidOrderId];
                        bidOrder.order.addressId = value.Address;
                        bidOrder.order.term.amount = value.Amount;
                        bidOrder.order.term.interest = value.Interest;
                        bidOrder.order.term.fee = value.Fee;
                        bidOrder.order.expiration = value.Expiration.ToString();
                        bidOrder.block = value.Block;
                    }

                    offer.askOrder = askOrder;
                    offer.bidOrder = bidOrder;
                    ret.Add(offer);
                }
                return Json(ret);
            }
            catch (Exception)
            {
                return StatusCode(503, unexpectedError);
            }
        }

        [HttpPost("CreateDealOrder")]
        public IActionResult PostCreateDealOrder(Models.CreateDealQueryParam queryParam)
        {
            //creditcoin AddDealOrder offerId expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddDealOrder");
            args.Add(queryParam.query.offerId);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("RetrieveDeals/{sighash}")]
        public IActionResult GetRetrieveDeals(string sighash)
        {
            //show NewDeals sighash
            return GetDeals(sighash, "NewDeals");
        }

        [HttpGet("RetrieveLoans/{sighash}")]
        public IActionResult GetRetrieveLoans(string sighash)
        {
            //show CurrentLoans sighash
            return GetDeals(sighash, "CurrentLoans");
        }

        [HttpGet("RetrieveLockedLoans/{sighash}")]
        public IActionResult GetRetrieveLockedLoans(string sighash)
        {
            //show CurrentLoans sighash
            return GetDeals(sighash, "LockedLoans");
        }

        private IActionResult GetDeals(string sighash, string filter)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add(filter);
            args.Add(sighash);
            var dealOrders = new cccore.Core.DealOrders();
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link, dealOrders);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                var ret = new List<ccaas.Models.DealResponse>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    var deal = new ccaas.Models.DealResponse() { id = output[i], term = new Models.Term() };
                    {
                        var value = dealOrders.dealOrders[output[i]];
                        deal.srcAddressId = value.SrcAddress;
                        deal.dstAddressId = value.DstAddress;
                        deal.term.amount = value.Amount;
                        deal.term.interest = value.Interest;
                        deal.term.maturity = value.Maturity;
                        deal.term.fee = value.Fee;
                        deal.expiration = value.Expiration.ToString();
                        deal.block = value.Block;
                    }

                    ret.Add(deal);
                }
                return Json(ret);
            }
            catch (Exception)
            {
                return StatusCode(503, unexpectedError);
            }
        }

        [HttpGet("RetrieveAddresses/{sighash}")]
        public IActionResult GetRetrieveAddresses(string sighash)
        {
            //list Addresses
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("list");
            args.Add("Addresses");
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                //ret.Add($"address({objid}) blockchain:{address.Blockchain} value:{address.Value} network:{address.Network} sighash:{address.Sighash}");
                var ret = new List<ccaas.Models.AddressResponse>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    var addressComponents = output[i].Split(' ');
                    if (addressComponents.Length != 5)
                        return StatusCode(503, unexpectedError);

                    var cur = 4;
                    var curSighash = getValue(addressComponents[cur--], "sighash");
                    if (curSighash.Equals(sighash))
                    {
                        string id;
                        {
                            const string addressPrefix = "address(";
                            if (!addressComponents[0].StartsWith(addressPrefix) || !addressComponents[0].EndsWith(")"))
                                return StatusCode(503, unexpectedError);
                            id = addressComponents[0].Substring(addressPrefix.Length, addressComponents[0].Length - addressPrefix.Length - 1);
                        }

                        var address = new ccaas.Models.AddressResponse() { id = id };

                        address.network = getValue(addressComponents[cur--], "network");
                        address.value = getValue(addressComponents[cur], "value");

                        ret.Add(address);
                    }
                }
                return Json(ret);
            }
            catch (Exception)
            {
                return StatusCode(503, unexpectedError);
            }
        }

        [HttpPost("RegisterTransfer")]
        public ActionResult<string> PostRegisterTransfer(Models.TransferQueryParam queryParam)
        {
            //erc20 RegisterTransfer gain orderId
            // or
            //creditcoin RegisterTransfer gain orderId txid
            // or
            //ethless RegisterTransfer gain orderId fee sig

            if (!string.IsNullOrEmpty(queryParam.query.continuation))
                return BadRequest("'continuation' cannot be set");
            var args = new List<string>();
            var msg = checkTransferParameters(queryParam, args, out Signer signer);
            if (msg != null)
                return BadRequest(msg);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out _, queryParam.query.ethKey, out string link);
            Debug.Assert(output == null && link != null || link == null);
            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 && output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            if (output.Count == 2)
                return Json(new Models.ContinuationResponse { reason = "waitingForeignBlockchain", waitingForeignBlockchain = output[0] });

            return Ok();
        }

        [HttpPost("CompleteRegisterTransfer")]
        public IActionResult PostCompleteRegisterTransfer(Models.TransferQueryParam queryParam)
        {
            //erc20 RegisterTransfer gain orderId
            // or
            //creditcoin RegisterTransfer gain orderId txid
            // or
            //ethless RegisterTransfer gain orderId fee

            if (string.IsNullOrEmpty(queryParam.query.continuation))
                return BadRequest("'continuation' must be set");

            var args = new List<string>();
            var msg = checkTransferParameters(queryParam, args, out Signer signer);
            if (msg != null)
                return BadRequest(msg);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, queryParam.query.continuation, signer, out _, queryParam.query.ethKey, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 && output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]); //TODO process separately if tx doesn't exist? 404 in docs

            if (output.Count == 2)
                return Json(new Models.ContinuationResponse { reason = "waitingForeignBlockchain", waitingForeignBlockchain = output[0] });

            return Ok();
        }

        private static string checkTransferParameters(Models.TransferQueryParam queryParam, List<string> args, out Signer signer)
        {
            signer = null;

            string txid = queryParam.query.txid;
            if (txid != null && txid.Equals(string.Empty))
                txid = null;
            string feeString = queryParam.query.fee;
            if (feeString != null && feeString.Equals(string.Empty))
                feeString = null;

            if (txid == null)
            {
                if (string.IsNullOrEmpty(queryParam.query.ethKey))
                    return missingParameters;
                if (feeString != null) // ethless transfer
                {
                    BigInteger fee;
                    if (!BigInteger.TryParse(feeString, out fee))
                        return "'fee' must be numeric";
                    if (fee < minimalFee)
                        return "'fee' is too small";
                }
                else
                {
                    if (feeString != null)
                        return "'fee' must be set only for ethless transfers";
                }
            }
            else
            {
                if (feeString != null)
                    return "'fee' cannot be set if 'txid' is set";
                if (!string.IsNullOrEmpty(queryParam.query.ethKey))
                    return "'ethKey' cannot be set if 'txid' is set";
            }

            if (txid != null)
            {
                args.Add("creditcoin");
            }
            else
            {
                if (feeString != null)
                    args.Add("ethless");
                else
                    args.Add("erc20");
            }
            args.Add("RegisterTransfer");
            args.Add(queryParam.query.gain);
            args.Add(queryParam.query.dealOrderId);
            if (txid != null)
                args.Add(txid);
            else if (feeString != null)
                args.Add(feeString);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return missingParameters;
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return message;
            signer = cccore.Core.getSigner(config, key);

            return null;
        }

        [HttpGet("ShowTransfer/{sighash}")]
        public IActionResult GetShowTransfer(string sighash, string dealOrderId)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Transfer");
            args.Add(sighash);
            args.Add(dealOrderId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);
            Debug.Assert(output.Count == 1 || output.Count == 2);

            if (output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
            {
                if (output[0].Equals("Success"))
                    return StatusCode(404, "Transfer not found for the deal order ID provided");
                return statusCodeByMsg(output[0]);
            }

            return Ok(output[0]);
        }

        [HttpPost("UpdateInvestment")]
        public IActionResult PostUpdateInvestment(Models.UpdateQueryParam queryParam)
        {
            //creditcoin CompleteDealOrder dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("CompleteDealOrder");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("LockDealOrder")]
        public IActionResult PostLockDealOrder(Models.LockQueryParam queryParam)
        {
            //creditcoin LockDealOrder dealOrderId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("LockDealOrder");
            args.Add(queryParam.query.dealOrderId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("UpdateRepayment")]
        public IActionResult PostUpdateRepayment(Models.UpdateQueryParam queryParam)
        {
            //creditcoin CloseDealOrder dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("CloseDealOrder");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("UpdateExemption")]
        public IActionResult PostUpdateExemption(Models.UpdateQueryParam queryParam)
        {
            //creditcoin Exempt dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("Exempt");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return BadRequest(missingParameters);
            }

            string key = queryParam.key;
            string message = ValidateKey(key);
            if (message != null)
                return BadRequest(message);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool _, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        private ObjectResult statusCodeByMsg(string error)
        {
            return StatusCode(error.StartsWith(unexpectedError)? 503: 500, error);
        }
    }
}
