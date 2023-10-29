using ChessChallenge.API;
using System;
using System.Collections.Generic;
using System.Linq;
using static ChessChallenge.API.BitboardHelper;


/// <summary>
/// Your task is to implement this file.
/// </summary>
public class Evaluator : IEvaluator
{
    // Due to the rules of the challenge and how token counting works, evaluation constants are packed into C# decimals,
    // as they allow the most efficient (12 usable bits per token).
    // The ordering is as follows: Midgame term 1, endgame term 1, midgame, term 2, endgame term 2...
    static sbyte[] extracted = new [] { 4835740172228143389605888m, 1862983114964290202813595648m, 6529489037797228073584297991m, 6818450810788061916507740187m, 7154536855449028663353021722m, 14899014974757699833696556826m, 25468819436707891759039590695m, 29180306561342183501734565961m, 944189991765834239743752701m, 4194697739m, 4340114601700738076711583744m, 3410436627687897068963695623m, 11182743911298765866015857947m, 10873240011723255639678263585m, 17684436730682332602697851426m, 17374951722591802467805509926m, 31068658689795177567161113954m, 1534136309681498319279645285m, 18014679997410182140m, 1208741569195510172352512m, 13789093343132567021105512448m, 6502873946609222871099113472m, 1250m }.SelectMany(x => decimal.GetBits(x).Take(3).SelectMany(y => (sbyte[])(Array)BitConverter.GetBytes(y))).ToArray();

    // After extracting the raw mindgame/endgame terms, we repack it into integers of midgame/endgame pairs.
    // The scheme in bytes (assuming little endian) is: 00 EG 00 MG
    // The idea of this is that we can do operations on both midgame and endgame values simultaneously, preventing the need
    // for evaluation for separate mid-game / end-game terms.
    int[] evalValues = Enumerable.Range(0, 138).Select(i => extracted[i * 2] | extracted[i * 2 + 1] << 16).ToArray();

    public int Evaluate(Board board, Timer timer)
    {
        // We use 15 tempo for evaluation for mid-game, 0 for end-game.
        int score = 15,
            phase = 0;

        // This is a tapered evaluation, meaning that each term has a midgame (or more accurately early-game) and end-game value.
        // After the evaluation is done, scores are interpolated according to phase values. Read more: https://www.chessprogramming.org/Tapered_Eval
        // This evaluation is similar to many other evaluations with some differences to save bytes.
        // It is tuned with a method called Texel Tuning using my project at https://github.com/GediminasMasaitis/texel-tuner
        // More info about Texel tuning at https://www.chessprogramming.org/Texel%27s_Tuning_Method,
        // and specifically the implementation in my tuner: https://github.com/AndyGrant/Ethereal/blob/master/Tuning.pdf
        // The evaluation is inlined into search to preserve bytes, and to keep some information (phase) around for later use.
        foreach (bool isWhite in new[] {!board.IsWhiteToMove, board.IsWhiteToMove})
        {
            score = -score;

            ulong bitboard = isWhite ? board.WhitePiecesBitboard : board.BlackPiecesBitboard,
                  sideBB   = bitboard;

            // This and the following line is an efficient way to loop over each piece of a certain type.
            // Instead of looping each square, we can skip empty squares by looking at a bitboard of each piece,
            // and incrementally removing squares from it. More information: https://www.chessprogramming.org/Bitboards
            while (bitboard != 0)
            {
                int sq = ClearAndGetIndexOfLSB(ref bitboard),
                    pieceIndex = (int)board.GetPiece(new (sq)).PieceType;

                // Open files, doubled pawns
                // We evaluate how much each piece wants to be in an open/semi-open file (both merged to save tokens).
                // We exclude the current piece's square from being considered, this enables a trick to save tokens:
                // for pawns, an open file means it is not a doubled pawn, so it acts as a doubled pawn detection for free.
                if ((0x101010101010101UL << sq % 8 & ~(1UL << sq) & board.GetPieceBitboard(PieceType.Pawn, isWhite)) == 0)
                    score += evalValues[126 + pieceIndex];

                // For bishop, rook, queen and king. ELO tests proved that mobility for other pieces are not worth considering.
                if (pieceIndex > 2)
                {
                    // Mobility
                    // The more squares you are able to attack, the more flexible your position is.
                    var mobility = GetPieceAttacks((PieceType)pieceIndex, new (sq), board, isWhite) & ~sideBB;
                    score += evalValues[112 + pieceIndex] * GetNumberOfSetBits(mobility)
                             // King attacks
                             // If your pieces' mobility intersects the opponent king's mobility, this means you are attacking
                             // the king, and this is worth evaluating separately.
                           + evalValues[119 + pieceIndex] * GetNumberOfSetBits(mobility & GetKingAttacks(board.GetKingSquare(!isWhite)));
                }

                // Flip square if black.
                // This is needed for piece square tables (PSTs), because they are always written from the side that is playing.
                if (!isWhite) sq ^= 56;

                // We count the phase of the current position.
                // The phase represents how much we are into the end-game in a gradual way. 24 = all pieces on the board, 0 = only pawns/kings left.
                // This is a core principle of tapered evaluation. We increment phase for each piece for both sides based on it's importance:
                // None: 0 (obviously)
                // Pawn: 0
                // Knight: 1
                // Bishop: 1
                // Rook: 2
                // Queen: 4
                // King: 0 (because checkmate and stalemate has its own special rules late on)
                // These values are encoded in the decimals mentioned before and aren't explicit in the engine's code.
                phase += evalValues[pieceIndex];

                // Material and PSTs
                // PST mean "piece-square tables", it is naturally better for a piece to be on a specific square.
                // More: https://www.chessprogramming.org/Piece-Square_Tables
                // In this engine, in order to save tokens, the concept of "material" has been removed.
                // Instead, each square for each piece has a higher value adjusted to the type of piece that occupies it.
                // In order to fit in 1 byte per row/column, the value of each row/column has been divided by 8,
                // and here multiplied by 8 (<< 3 is equivalent but ends up 1 token smaller).
                // Additionally, each column/row, or file/rank is evaluated, as opposed to every square individually,
                // which is only ~20 ELO weaker compared to full PSTs and saves a lot of tokens.
                score += evalValues[pieceIndex * 8 + sq / 8]
                       + evalValues[56 + pieceIndex * 8 + sq % 8]
                       << 3;
            }
        }
        
        // Here we interpolate the midgame/endgame scores from the single variable to a proper integer that can be used by search
        return ((short)score * phase + (score + 0x8000 >> 16) * (24 - phase)) / 24;
        
    }
}