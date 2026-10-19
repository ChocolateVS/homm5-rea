// Finds 4-byte float (or int) constants in initialized memory and lists code references to them.
// Usage: -postScript FindFloatRefs.java <outFile> <f:70.0|i:70> [...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class FindFloatRefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Listing listing = currentProgram.getListing();
        for (int i = 1; i < a.length; i++) {
            int bits = a[i].startsWith("f:") ? Float.floatToIntBits(Float.parseFloat(a[i].substring(2))) : Integer.parseInt(a[i].substring(2));
            byte[] pat = { (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24) };
            out.println("VALUE " + a[i]);
            Address p = currentProgram.getMinAddress();
            int n = 0;
            while (n < 400) {
                p = currentProgram.getMemory().findBytes(p, pat, null, true, monitor);
                if (p == null) break;
                n++;
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(p)) {
                    Function f = listing.getFunctionContaining(r.getFromAddress());
                    if (f != null) out.println("  @" + p + " <- " + r.getFromAddress() + " in " + f.getName() + "@" + f.getEntryPoint());
                }
                p = p.add(1);
            }
        }
        out.close();
    }
}
