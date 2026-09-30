using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Plugins.TestApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TestController : ControllerBase
    {
        [HttpGet("Test1")]
        public void Test1()
        {
            PluginsTest.Test1();
        }

        [HttpGet("Test2Sandbox")]
        public async Task Test2Sandbox()
        {
           await PluginsTest.Test2Sandbox();
        }

        [HttpGet("Test3BuilderParityTest")]
        public async Task Test3BuilderParityTest()
        {
            await PluginsTest.Test3BuilderParityTest();
        }
    }
}
