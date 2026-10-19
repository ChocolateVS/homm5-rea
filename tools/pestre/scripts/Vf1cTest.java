// Lists CALL [reg+0x1c] immediately followed by TEST AL,AL (bool virtual call sites) and refs to given addrs.
// Usage: -postScript Vf1cTest.java <out> <addr...>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
public class Vf1cTest extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Listing l = currentProgram.getListing();
        for (int i = 1; i < a.length; i++)
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(toAddr(a[i]))) {
                Function f = l.getFunctionContaining(r.getFromAddress());
                out.println("REF " + a[i] + " from " + r.getFromAddress() + " " + r.getReferenceType() + " in " + (f == null ? "-" : f.getEntryPoint()));
            }
        InstructionIterator it = l.getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            String s = ins.toString();
            if (!s.startsWith("CALL dword ptr [") || !s.endsWith("+ 0x1c]")) continue;
            Instruction n = ins.getNext();
            if (n == null || !n.toString().equals("TEST AL,AL")) continue;
            Instruction p = ins.getPrevious(), pp = p == null ? null : p.getPrevious();
            Instruction j = n.getNext(), k = j == null ? null : j.getNext(), k2 = k == null ? null : k.getNext(), k3 = k2 == null ? null : k2.getNext();
            Function f = l.getFunctionContaining(ins.getAddress());
            out.println(ins.getAddress() + " in " + (f == null ? "-" : f.getEntryPoint()) + " | " + (pp==null?"":pp) + " ; " + (p==null?"":p) + " ; " + s + " ; " + j + " ; " + k + " ; " + k2 + " ; " + k3);
        }
        out.close();
    }
}
