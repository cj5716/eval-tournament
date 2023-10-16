﻿using ChessChallenge.API;
using System;
using Chess_Challenge.Example;

namespace ChessChallenge.Example
{
    /// <summary>
    /// The same search as MyBot.cs.
    /// </summary>
    public class EvilBot : IChessBot
    {

        Move bestmoveRoot = Move.NullMove;

        private IEvaluator evaluator = new EvilBotEvaluator();

        // https://www.chessprogramming.org/Transposition_Table
        struct TTEntry {
            public ulong key;
            public Move move;
            public int depth, score, bound;
            public TTEntry(ulong _key, Move _move, int _depth, int _score, int _bound) {
                key = _key; move = _move; depth = _depth; score = _score; bound = _bound;
            }
        }

        const int entries = (1 << 20);
        TTEntry[] tt = new TTEntry[entries];

        // https://www.chessprogramming.org/Negamax
        // https://www.chessprogramming.org/Quiescence_Search
        public int Search(Board board, Timer timer, int alpha, int beta, int depth, int ply) {
            ulong key = board.ZobristKey;
            bool qsearch = depth <= 0;
            bool notRoot = ply > 0;
            int best = -30000;

            // Check for repetition (this is much more important than material and 50 move rule draws)
            if(notRoot && board.IsRepeatedPosition())
                return 0;

            TTEntry entry = tt[key % entries];

            // TT cutoffs
            if(notRoot && entry.key == key && entry.depth >= depth && (
                entry.bound == 3 // exact score
                    || entry.bound == 2 && entry.score >= beta // lower bound, fail high
                    || entry.bound == 1 && entry.score <= alpha // upper bound, fail low
            )) return entry.score;

            int eval = evaluator.Evaluate(board, timer);

            // Quiescence search is in the same function as negamax to save tokens
            if(qsearch) {
                best = eval;
                if(best >= beta) return best;
                alpha = Math.Max(alpha, best);
            }

            // Generate moves, only captures in qsearch
            Move[] moves = board.GetLegalMoves(qsearch);
            int[] scores = new int[moves.Length];

            // Score moves
            for(int i = 0; i < moves.Length; i++) {
                Move move = moves[i];
                // TT move
                if(move == entry.move) scores[i] = 1000000;
                // https://www.chessprogramming.org/MVV-LVA
                else if(move.IsCapture) scores[i] = 100 * (int)move.CapturePieceType - (int)move.MovePieceType;
            }

            Move bestMove = Move.NullMove;
            int origAlpha = alpha;

            // Search moves
            for(int i = 0; i < moves.Length; i++) {
                if(timer.MillisecondsElapsedThisTurn >= timer.MillisecondsRemaining / 30) return 30000;

                // Incrementally sort moves
                for(int j = i + 1; j < moves.Length; j++) {
                    if(scores[j] > scores[i])
                        (scores[i], scores[j], moves[i], moves[j]) = (scores[j], scores[i], moves[j], moves[i]);
                }

                Move move = moves[i];
                board.MakeMove(move);
                int score = -Search(board, timer, -beta, -alpha, depth - 1, ply + 1);
                board.UndoMove(move);

                // New best move
                if(score > best) {
                    best = score;
                    bestMove = move;
                    if(ply == 0) bestmoveRoot = move;

                    // Improve alpha
                    alpha = Math.Max(alpha, score);

                    // Fail-high
                    if(alpha >= beta) break;

                }
            }

            // (Check/Stale)mate
            if(!qsearch && moves.Length == 0) return board.IsInCheck() ? -30000 + ply : 0;

            // Did we fail high/low or get an exact score?
            int bound = best >= beta ? 2 : best > origAlpha ? 3 : 1;

            // Push to TT
            tt[key % entries] = new TTEntry(key, bestMove, depth, best, bound);

            return best;
        }

        public (Move, int) Think(Board board, Timer timer)
        {
            bestmoveRoot = Move.NullMove;
            int score = 0;
            // https://www.chessprogramming.org/Iterative_Deepening
            for(int depth = 1; depth <= 50; depth++) {
                int iterationScore = Search(board, timer, -30000, 30000, depth, 0);

                // Out of time
                if(timer.MillisecondsElapsedThisTurn >= timer.MillisecondsRemaining / 30)
                    break;
                score = iterationScore;
            }
            return (bestmoveRoot.IsNull ? board.GetLegalMoves()[0] : bestmoveRoot, score);
        }
    }
}