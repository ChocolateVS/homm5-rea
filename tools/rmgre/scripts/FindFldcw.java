// Lists FLDCW instructions whose operand is not ESP/EBP-relative, and functions named like controlfp.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
public class FindFldcw extends GhidraScript {
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(new FileWriter(getScriptArgs()[0]));
        Listing l = currentProgram.getListing();
        InstructionIterator it = l.getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            if (!ins.getMnemonicString().equals("FLDCW")) continue;
            String op = ins.getDefaultOperandRepresentation(0);
            if (op.contains("ESP") || op.contains("EBP")) continue;
            Function f = l.getFunctionContaining(ins.getAddress());
            out.println(ins.getAddress() + "  " + ins + "  in " + (f == null ? "?" : f.getName() + "@" + f.getEntryPoint()));
        }
        SymbolIterator si = currentProgram.getSymbolTable().getAllSymbols(true);
        while (si.hasNext()) { Symbol s = si.next(); String n = s.getName().toLowerCase(); if (n.contains("control") || n.contains("fpreset") || n.contains("fpu")) out.println("SYM " + s.getName() + " @ " + s.getAddress()); }
        out.close();
    }
}
