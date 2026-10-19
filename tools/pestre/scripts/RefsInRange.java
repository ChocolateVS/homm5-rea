// Lists addresses in [start,end] that have references to them (e.g. to find a vtable start).
// Usage: -postScript RefsInRange.java <outFile> <start> <end>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class RefsInRange extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Address s = toAddr(a[1]), e = toAddr(a[2]);
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (Address x = s; x.compareTo(e) <= 0; x = x.add(4)) {
            for (Reference r : rm.getReferencesTo(x)) {
                Function f = currentProgram.getListing().getFunctionContaining(r.getFromAddress());
                out.println(x + " <- " + r.getFromAddress() + " " + r.getReferenceType() + " " + (f == null ? "" : f.getName()));
            }
        }
        out.close();
    }
}
