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
        private static HttpClient httpClient = new HttpClient();

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
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
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

                    //list AskOrders id
                    var argsAskOrder = new List<string>();
                    argsAskOrder.Add("list");
                    argsAskOrder.Add("AskOrders");
                    argsAskOrder.Add(ids[0]);
                    List<string> outputAskOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsAskOrder.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputAskOrders.Count != 2 || outputAskOrders[0].StartsWith("Error") || !outputAskOrders[1].Equals("Success"))
                        return statusCodeByMsg(outputAskOrders[0]);

                    //ret.Add($"askOrder({objid}) blockchain:{askOrder.Blockchain} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                    var addressPrefix = "address";
                    var idx = outputAskOrders[0].LastIndexOf(addressPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore askOrder and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var askOrderComponents = outputAskOrders[0].Substring(idx).Split(' ');
                    if (askOrderComponents.Length != 8)
                        return StatusCode(503, unexpectedError);

                    var askOrderId = ids[0];
                    var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };

                    var cur = 0;
                    askOrder.order.addressId = getValue(askOrderComponents[cur++], "address");
                    askOrder.order.term.amount = getValue(askOrderComponents[cur++], "amount");
                    askOrder.order.term.interest = getValue(askOrderComponents[cur++], "interest");
                    askOrder.order.term.maturity = getValue(askOrderComponents[cur++], "maturity");
                    askOrder.order.term.fee = getValue(askOrderComponents[cur++], "fee");
                    askOrder.order.expiration = getValue(askOrderComponents[cur++], "expiration");
                    askOrder.block = getValue(askOrderComponents[cur], "block");

                    //list bidOrders id
                    var argsBidOrder = new List<string>();
                    argsBidOrder.Add("list");
                    argsBidOrder.Add("BidOrders");
                    argsBidOrder.Add(ids[1]);
                    List<string> outputBidOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsBidOrder.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputBidOrders.Count != 2 || outputBidOrders[0].StartsWith("Error") || !outputBidOrders[1].Equals("Success"))
                        return statusCodeByMsg(outputBidOrders[0]);

                    //ret.Add($"bidOrder({objid}) blockchain:{bidOrder.Blockchain} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                    idx = outputBidOrders[0].LastIndexOf(addressPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore bidOrder and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var bidOrderComponents = outputBidOrders[0].Substring(idx).Split(' ');
                    if (bidOrderComponents.Length != 8)
                        return StatusCode(503, unexpectedError);

                    var bidOrderId = ids[1];
                    var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };

                    cur = 0;
                    bidOrder.order.addressId = getValue(bidOrderComponents[cur++], "address");
                    bidOrder.order.term.amount = getValue(bidOrderComponents[cur++], "amount");
                    bidOrder.order.term.interest = getValue(bidOrderComponents[cur++], "interest");
                    bidOrder.order.term.maturity = getValue(bidOrderComponents[cur++], "maturity");
                    bidOrder.order.term.fee = getValue(bidOrderComponents[cur++], "fee");
                    bidOrder.order.expiration = getValue(bidOrderComponents[cur++], "expiration");
                    bidOrder.block = getValue(bidOrderComponents[cur], "block");

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
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                var ret = new List<ccaas.Models.OfferResponse>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    //list Offers id
                    var argsOffer = new List<string>();
                    argsOffer.Add("list");
                    argsOffer.Add("Offers");
                    argsOffer.Add(output[i]);
                    List<string> outputOffers = cccore.Core.Run(httpClient, creditcoinUrl, argsOffer.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputOffers.Count < 2 || outputOffers[0].StartsWith("Error") || !outputOffers[outputOffers.Count - 1].Equals("Success"))
                        return statusCodeByMsg(outputOffers[1]);

                    //ret.Add($"offer({objid}) blockchain:{offer.Blockchain} askOrder:{offer.AskOrder} bidOrder:{offer.BidOrder} expiration:{offer.Expiration} block:{offer.Block}");
                    var askOrderPrefix = "askOrder";
                    var idx = outputOffers[0].LastIndexOf(askOrderPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore offer and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var offerComponents = outputOffers[0].Substring(idx).Split(' ');
                    if (offerComponents.Length != 4)
                        return StatusCode(503, unexpectedError);
                    var offer = new ccaas.Models.OfferResponse() { id = output[i] };

                    var cur = 0;
                    string askOrderId = getValue(offerComponents[cur++], "askOrder");
                    string bidOrderId = getValue(offerComponents[cur++], "bidOrder");
                    offer.expiration = getValue(offerComponents[cur++], "expiration");
                    offer.block = getValue(offerComponents[cur], "block");

                    //list AskOrders id
                    var argsAskOrder = new List<string>();
                    argsAskOrder.Add("list");
                    argsAskOrder.Add("AskOrders");
                    argsAskOrder.Add(askOrderId);
                    List<string> outputAskOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsAskOrder.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputAskOrders.Count != 2 || outputAskOrders[0].StartsWith("Error") || !outputAskOrders[1].Equals("Success"))
                        return statusCodeByMsg(outputAskOrders[0]);


                    //ret.Add($"askOrder({objid}) blockchain:{askOrder.Blockchain} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                    var addressPrefix = "address";
                    idx = outputAskOrders[0].LastIndexOf(addressPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore askOrder and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var askOrderComponents = outputAskOrders[0].Substring(idx).Split(' ');
                    if (askOrderComponents.Length != 8)
                        return StatusCode(503, unexpectedError);

                    var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };

                    cur = 0;
                    askOrder.order.addressId = getValue(askOrderComponents[cur++], "address");
                    askOrder.order.term.amount = getValue(askOrderComponents[cur++], "amount");
                    askOrder.order.term.interest = getValue(askOrderComponents[cur++], "interest");
                    cur++; //ignoring maturity
                    askOrder.order.term.fee = getValue(askOrderComponents[cur++], "fee");
                    askOrder.order.expiration = getValue(askOrderComponents[cur++], "expiration");
                    askOrder.block = getValue(askOrderComponents[cur], "block");

                    //list bidOrders id
                    var argsBidOrder = new List<string>();
                    argsBidOrder.Add("list");
                    argsBidOrder.Add("BidOrders");
                    argsBidOrder.Add(bidOrderId);
                    List<string> outputBidOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsBidOrder.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputBidOrders.Count != 2 || outputBidOrders[0].StartsWith("Error") || !outputBidOrders[1].Equals("Success"))
                        return statusCodeByMsg(outputBidOrders[0]);

                    //ret.Add($"bidOrder({objid}) blockchain:{bidOrder.Blockchain} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                    idx = outputBidOrders[0].LastIndexOf(addressPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore bidOrder and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var bidOrderComponents = outputBidOrders[0].Substring(idx).Split(' ');
                    if (bidOrderComponents.Length != 8)
                        return StatusCode(503, unexpectedError);

                    var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };

                    cur = 0;
                    bidOrder.order.addressId = getValue(bidOrderComponents[cur++], "address");
                    bidOrder.order.term.amount = getValue(bidOrderComponents[cur++], "amount");
                    bidOrder.order.term.interest = getValue(bidOrderComponents[cur++], "interest");
                    cur++; //ignoring maturity
                    bidOrder.order.term.fee = getValue(bidOrderComponents[cur++], "fee");
                    bidOrder.order.expiration = getValue(bidOrderComponents[cur++], "expiration");
                    bidOrder.block = getValue(bidOrderComponents[cur], "block");

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
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            try
            {
                var ret = new List<ccaas.Models.DealResponse>();
                for (int i = 0; i < output.Count - 1; ++i)
                {
                    //list Deals id
                    var argsDeal = new List<string>();
                    argsDeal.Add("list");
                    argsDeal.Add("DealOrders");
                    argsDeal.Add(output[i]);
                    List<string> outputDeals = cccore.Core.Run(httpClient, creditcoinUrl, argsDeal.ToArray(), config, false, pluginFolder, null, null, out bool _, null, out link);
                    Debug.Assert(output != null && link == null);
                    if (outputDeals.Count < 1 || outputDeals[0].StartsWith("Error") || !outputDeals[outputDeals.Count - 1].Equals("Success"))
                        return statusCodeByMsg(outputDeals[0]);

                    //ret.Add($"dealOrder({objid}) blockchain:{dealOrder.Blockchain} srcAddress:{dealOrder.SrcAddress} dstAddress:{dealOrder.DstAddress} amount:{dealOrder.Amount} interest:{dealOrder.Interest} maturity:{dealOrder.Maturity} fee:{dealOrder.Fee} expiration:{dealOrder.Expiration} block:{dealOrder.Block} loanTransfer:{(dealOrder.LoanTransfer.Equals(string.Empty) ? "*" : dealOrder.LoanTransfer)} repaymentTransfer:{(dealOrder.RepaymentTransfer.Equals(string.Empty) ? "*" : dealOrder.RepaymentTransfer)} lock:{(dealOrder.Lock.Equals(string.Empty) ? "*" : dealOrder.Lock)} sighash:{dealOrder.Sighash}");
                    var srcAddressPrefix = "srcAddress";
                    var idx = outputDeals[0].LastIndexOf(srcAddressPrefix);
                    if (idx == -1)
                        return StatusCode(503, unexpectedError);
                    // ignore dealOrder and blockchain (in case blockchain contains a combination of characters that may interfere with parsing, like space or colon
                    var dealComponents = outputDeals[0].Substring(idx).Split(' ');
                    if (dealComponents.Length != 12)
                        return StatusCode(503, unexpectedError);
                    var deal = new ccaas.Models.DealResponse() { id = output[i], term = new Models.Term() };

                    var cur = 0;
                    deal.srcAddressId = getValue(dealComponents[cur++], "srcAddress");
                    deal.dstAddressId = getValue(dealComponents[cur++], "dstAddress");
                    deal.term.amount = getValue(dealComponents[cur++], "amount");
                    deal.term.interest = getValue(dealComponents[cur++], "interest");
                    deal.term.maturity = getValue(dealComponents[cur++], "maturity");
                    deal.term.fee = getValue(dealComponents[cur++], "fee");
                    deal.expiration = getValue(dealComponents[cur++], "expiration");
                    deal.block = getValue(dealComponents[cur], "block");

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
                if (feeString != null) // ethless thansfer
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
