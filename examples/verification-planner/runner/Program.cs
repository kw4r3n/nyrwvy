using System;
using System.Linq;
using Nyrwvy.Generated;
internal static class Program {
    static readonly NyT_30316130623337302D346562302D376563642D623565332D336263316531306564313566[] Areas = [new NyV_30316130623337302D346562302D373231642D623237302D613365633437363133316335(), new NyV_30316130623337302D346562302D373336392D386534642D613935313131366334343534(), new NyV_30316130623337302D346562302D373430342D386631342D623238356338303232396631(), new NyV_30316130623337302D346562302D373838372D626332392D373963643361653866653938(), new NyV_30316130623337302D346562302D373966642D383634362D313839343265373066666566(), new NyV_30316130623337302D346562302D376539302D623435392D643133376562316231653662(), new NyV_30316130623337302D346562302D376639632D613837342D336230386261316635343365()];
    static readonly string[] Names = ["Backend", "EvaluationHarness", "Documentation", "AuthoringProtocol", "Persistence", "Semantics", "CI"];
    static string Run(int mask, bool agent, bool environment, bool regression) {
        var selected = Areas.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        var input = new NyT_30316130623337312D303535612D373738372D386562322D633731383537343139353533(f_30316130623337312D303535612D373137362D613437302D323630646631353262373631: regression, f_30316130623337312D303535612D373233332D626431302D663463343535656665313366: environment, f_30316130623337312D303535612D373533392D383432322D666337316664353438343466: agent, f_30316130623337312D303535612D376530632D626235612D323338616234396537643765: NyList<NyT_30316130623337302D346562302D376563642D623565332D336263316531306564313566>.Create(selected));
        return NyFunctions.NyF_30316130623337312D366330662D376436322D613666632D653838333563353836366136(NyFunctions.NyF_30316130623337312D353939302D373465382D623130372D356262353138343439376363(input));
    }
    static void Main(string[] args) {
        if (args.Length > 0 && args[0] != "--test") {
            int mask = 0;
            foreach (var name in args[0].Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                int index = Array.FindIndex(Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new ArgumentException("Unknown area: " + name);
                mask |= 1 << index;
            }
            Console.WriteLine(Run(mask, args.Contains("--agent"), args.Contains("--environment"), args.Contains("--regression")));
            return;
        }
        bool maintained = args.Contains("--maintained");
        int count = 0;
        for (int mask=0; mask<128; mask++)
        for (int flags=0; flags<8; flags++) {
            bool agent=(flags&1)!=0, environment=(flags&2)!=0, regression=(flags&4)!=0;
            bool extended = Names.Where((_,i)=>(mask&(1<<i))!=0).Any(n=>n is "Semantics" or "Backend" or "Persistence" || (maintained && n=="EvaluationHarness"));
            bool live=agent||environment;
            string expected = (extended,live) switch { (true,true)=>"fast + extended + live", (true,false)=>"fast + extended", (false,true)=>"fast + live", _=>"fast tests only" };
            string actual=Run(mask,agent,environment,regression);
            if(actual!=expected) throw new Exception($"mask={mask}, flags={flags}: {actual} != {expected}");
            count++;
        }
        Console.WriteLine($"PASS {count} exhaustive cases; maintained={maintained}");
    }
}
