// Lists callers (functions containing references) of each given function address.
// Usage: -postScript Callers.java <outFile> <addr> [addr...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class Callers extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Listing l = currentProgram.getListing();
        for (int i = 1; i < a.length; i++) {
            out.println("CALLERS OF " + a[i]);
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(toAddr(a[i]))) {
                Function f = l.getFunctionContaining(r.getFromAddress());
                out.println("  " + r.getFromAddress() + " " + r.getReferenceType() + " in " + (f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
            }
        }
        out.close();
    }
}
