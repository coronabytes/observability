using Microsoft.AspNetCore.Mvc;

namespace Core.Observability.SampleApp.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController(ILogger<WeatherForecastController> logger) : ControllerBase
    {
        [HttpGet(Name = "request")]
        public IActionResult Get()
        {
            return Ok();
        }

        [HttpPost("error")]
        public IActionResult Error()
        {
            logger.LogError("some error");

            return Ok();
        }

        [HttpPost("exception")]
        public IActionResult Exception()
        {
            throw new Exception("some exception");
        }
    }
}
