// Disassembles address ranges (disassembling undefined bytes first).
// Usage: -postScript Disasm.java <outFile> <start> <end> [<start> <end> ...]
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class Disasm extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Listing l = currentProgram.getListing();
        for (int i = 1; i + 1 < a.length; i += 2) {
            Address s = toAddr(a[i]), e = toAddr(a[i + 1]);
            AddressSet set = new AddressSet(s, e);
            new DisassembleCommand(set, null, true).applyTo(currentProgram, monitor);
            out.println("; ---- " + s + " .. " + e);
            for (Instruction ins : l.getInstructions(set, true)) {
                String extra = "";
                for (Reference r : ins.getReferencesFrom()) {
                    Data d = l.getDataAt(r.getToAddress());
                    if (d != null && d.hasStringValue()) extra += " ; \"" + d.getValue() + "\"";
                    Function f = l.getFunctionAt(r.getToAddress());
                    if (f != null) extra += " ; " + f.getName();
                }
                out.println(ins.getAddress() + "  " + ins + extra);
            }
        }
        out.close();
    }
}
