using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ccbe.Controllers
{
    /// <summary>
    /// Addresses entries
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class AddressesController : Controller
    {
        // GET api/addresses/<sighash>
        /// <summary>Wallet info</summary>
        /// <param name="sighash">A wallet's sighash - 60 hexadecimal digits</param>
        /// <returns>A new Address object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="400">If the sighash format is invalid</response>
        /// <response code="404">If a wallet for the sighash hasn't been created yet</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet("{sighash}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(503)]
        public IActionResult Get(string sighash)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest();

            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            var amount = Cache.GetWallet(sighash);
            if (amount == null)
                return NotFound();

            var address = new Models.Address { Amount = amount };
            return Json(address);
        }
    }
}