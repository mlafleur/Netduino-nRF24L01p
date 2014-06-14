using System.Threading;

namespace nRF24L01p
{
    public class Startup
    {
        public static void Main()
        {
            Thread.Sleep(1000);
            var prog = new Program();
            prog.Start();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}